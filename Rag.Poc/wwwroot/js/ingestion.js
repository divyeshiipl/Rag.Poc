const select = document.getElementById('projectSelect');
const processBtn = document.getElementById('processBtn');
const rechunkBtn = document.getElementById('rechunkBtn');
const status = document.getElementById('status');
const resultCard = document.getElementById('resultCard');
const resultEl = document.getElementById('result');

async function loadProjects() {
    try {
        const res = await fetch('/api/ProjectsQuery');
        const data = await res.json();
        data.forEach(p => {
            const opt = document.createElement('option');
            opt.value = p.id;
            opt.textContent = `${p.name} (${p.documentCount} docs)`;
            select.appendChild(opt);
        });
    } catch (err) {
        status.textContent = '❌ Failed to load projects: ' + err.message;
    }
}

async function run(url) {
    const id = select.value;
    if (!id) { status.textContent = 'Pick a project first.'; return; }

    processBtn.disabled = rechunkBtn.disabled = true;
    status.innerHTML = '<span class="spinner"></span> Working…';
    resultCard.style.display = 'none';

    try {
        const res = await fetch(url.replace('{id}', id), { method: 'POST' });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();
        resultEl.textContent = JSON.stringify(data, null, 2);
        resultCard.style.display = 'block';
        status.textContent = '✅ Done';
    } catch (err) {
        status.textContent = '❌ ' + err.message;
    } finally {
        processBtn.disabled = rechunkBtn.disabled = false;
    }
}

processBtn.addEventListener('click', () => run('/api/Ingestion/projects/{id}'));
rechunkBtn.addEventListener('click', () => run('/api/Ingestion/projects/{id}/rechunk'));

loadProjects();