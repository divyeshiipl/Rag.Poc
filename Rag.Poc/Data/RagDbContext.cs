using Microsoft.EntityFrameworkCore;
using Rag.Poc.Models;

namespace Rag.Poc.Data;

public class RagDbContext : DbContext
{
    public RagDbContext(
        DbContextOptions<RagDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        //modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Project>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<Document>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<DocumentChunk>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<Project>()
            .HasMany(x => x.Documents)
            .WithOne(x => x.Project)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Document>()
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
    }
}