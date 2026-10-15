const q = document.getElementById('query');
const btn = document.getElementById('searchBtn');
const status = document.getElementById('status');
const resultsCard = document.getElementById('resultsCard');
const resultsEl = document.getElementById('results');

async function search() {
    const query = q.value.trim();
    if (!query) return;

    btn.disabled = true;
    status.innerHTML = '<span class="spinner"></span> Searching…';
    resultsCard.style.display = 'none';

    try {
        const res = await fetch(`/api/DocumentSearch?query=${encodeURIComponent(query)}`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();

        resultsEl.innerHTML = '';
        if (!data.length) {
            resultsEl.innerHTML = '<p>No matches.</p>';
        } else {
            data.forEach(r => {
                const div = document.createElement('div');
                div.className = 'source';
                div.innerHTML = `
                    <div class="name">${escapeHtml(r.sectionTitle || 'Chunk')} · #${r.chunkIndex}</div>
                    <div class="loc">Document: ${escapeHtml(r.documentId)}</div>
                    <div style="margin-top:.4rem; white-space:pre-wrap;">${escapeHtml(r.content)}</div>`;
                resultsEl.appendChild(div);
            });
        }
        resultsCard.style.display = 'block';
        status.textContent = '';
    } catch (err) {
        status.textContent = '❌ ' + err.message;
    } finally {
        btn.disabled = false;
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
}

btn.addEventListener('click', search);
q.addEventListener('keydown', e => { if (e.key === 'Enter') search(); });