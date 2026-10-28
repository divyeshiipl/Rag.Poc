namespace Rag.Poc.Data;

public class RagDbContext : DbContext
{
    public RagDbContext(DbContextOptions<RagDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Models.Document> Documents => Set<Models.Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>().HasKey(x => x.Id);
        modelBuilder.Entity<Models.Document>().HasKey(x => x.Id);
        modelBuilder.Entity<DocumentChunk>().HasKey(x => x.Id);

        modelBuilder.Entity<Project>()
            .HasMany(x => x.Documents)
            .WithOne(x => x.Project)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Models.Document>()
            .HasMany(x => x.Chunks)
            .WithOne(x => x.Document)
            .HasForeignKey(x => x.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DocumentChunk>()
            .Property(x => x.Embedding)
            .HasColumnType("vector(384)");

        modelBuilder.Entity<DocumentChunk>()
            .Property(x => x.Content)
            .IsRequired();

        modelBuilder.Entity<DocumentChunk>()
            .Property(x => x.ChunkIndex)
            .IsRequired();

        modelBuilder.Entity<DocumentChunk>()
            .Property(x => x.TokenCount)
            .IsRequired();

        // Namespace for A/B index isolation
        modelBuilder.Entity<DocumentChunk>()
            .Property(x => x.Namespace)
            .HasMaxLength(100)
            .HasDefaultValue("default");

        // Full-text search vector (BM25-ish via Postgres ts_rank)
        modelBuilder.Entity<DocumentChunk>()
            .Property(x => x.SearchVector)
            .HasColumnType("tsvector");

        modelBuilder.Entity<DocumentChunk>()
            .HasIndex(x => x.Namespace);

        modelBuilder.Entity<DocumentChunk>()
            .HasIndex(x => x.DocumentId);

        modelBuilder.Entity<DocumentChunk>()
            .HasIndex(x => x.SearchVector)
            .HasMethod("GIN");

        modelBuilder.Entity<DocumentChunk>()
            .HasIndex(x => x.Embedding)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops");
    }
}