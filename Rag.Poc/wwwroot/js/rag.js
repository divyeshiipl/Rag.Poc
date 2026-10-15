document.addEventListener('DOMContentLoaded', () => {
    console.log('[rag.js] loaded');

    const q = document.getElementById('question');
    const askBtn = document.getElementById('askBtn');
    const clearBtn = document.getElementById('clearBtn');
    const status = document.getElementById('status');
    const answerCard = document.getElementById('answerCard');
    const answerEl = document.getElementById('answer');
    const sourcesEl = document.getElementById('sources');
    const execTimeEl = document.getElementById('execTime');

    if (!q || !askBtn || !clearBtn) {
        console.error('[rag.js] Required elements missing.', {
            q: !!q, askBtn: !!askBtn, clearBtn: !!clearBtn
        });
        return;
    }

    async function ask() {
        const question = q.value.trim();
        if (!question) { status.textContent = 'Type a question first.'; return; }

        askBtn.disabled = true;
        status.innerHTML = '<span class="spinner"></span> Thinking…';
        answerCard.style.display = 'none';

        try {
            const res = await fetch(`/api/Rag?question=${encodeURIComponent(question)}`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();

            answerEl.textContent = data.answer || '(no answer)';
            sourcesEl.innerHTML = '';
            (data.sources || []).forEach(s => {
                const div = document.createElement('div');
                div.className = 'source';
                div.innerHTML = `
                    <div class="name">${escapeHtml(s.documentName || '(unnamed)')}</div>
                    <div class="loc">
                        chunk #${s.chunkIndex}
                        ${s.section ? ' · ' + escapeHtml(s.section) : ''}
                        ${s.sourceLocation ? ' · ' + escapeHtml(s.sourceLocation) : ''}
                    </div>`;
                sourcesEl.appendChild(div);
            });

            execTimeEl.textContent = data.executionTimeMinutes != null
                ? `Execution time: ${data.executionTimeMinutes} min` : '';

            answerCard.style.display = 'block';
            status.textContent = '';
        } catch (err) {
            console.error('[rag.js] ask failed', err);
            status.textContent = '❌ ' + err.message;
        } finally {
            askBtn.disabled = false;
        }
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[c]));
    }

    askBtn.addEventListener('click', ask);
    clearBtn.addEventListener('click', () => {
        q.value = '';
        answerCard.style.display = 'none';
        status.textContent = '';
    });
    q.addEventListener('keydown', e => {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) ask();
    });

    console.log('[rag.js] handlers attached');
});