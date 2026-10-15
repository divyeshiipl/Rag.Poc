const nameEl = document.getElementById('name');
const descEl = document.getElementById('description');
const folderEl = document.getElementById('folder');
const createBtn = document.getElementById('createBtn');
const refreshBtn = document.getElementById('refreshBtn');
const status = document.getElementById('status');
const rows = document.getElementById('projectRows');

async function load() {
    rows.innerHTML = '<tr><td colspan="5">Loading…</td></tr>';
    try {
        const res = await fetch('/api/ProjectsQuery');
        const data = await res.json();
        if (!data.length) {
            rows.innerHTML = '<tr><td colspan="5">No projects yet.</td></tr>';
            return;
        }
        rows.innerHTML = data.map(p => `
            <tr>
                <td>${escapeHtml(p.name)}</td>
                <td>${escapeHtml(p.description || '')}</td>
                <td>${escapeHtml(p.documentFolder || '')}</td>
                <td>${p.documentCount}</td>
                <td>${new Date(p.createdAtUtc).toLocaleString()}</td>
            </tr>`).join('');
    } catch (err) {
        rows.innerHTML = `<tr><td colspan="5">❌ ${escapeHtml(err.message)}</td></tr>`;
    }
}

async function create() {
    const body = {
        name: nameEl.value.trim(),
        description: descEl.value.trim() || null,
        documentFolder: folderEl.value.trim()
    };
    if (!body.name || !body.documentFolder) {
        status.textContent = 'Name and folder are required.';
        return;
    }

    createBtn.disabled = true;
    status.innerHTML = '<span class="spinner"></span> Creating…';
    try {
        const res = await fetch('/api/Projects', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        status.textContent = '✅ Created';
        nameEl.value = descEl.value = folderEl.value = '';
        load();
    } catch (err) {
        status.textContent = '❌ ' + err.message;
    } finally {
        createBtn.disabled = false;
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
}

createBtn.addEventListener('click', create);
refreshBtn.addEventListener('click', load);
load();