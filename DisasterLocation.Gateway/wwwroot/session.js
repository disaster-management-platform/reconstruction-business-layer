window.platformFetch = async function (path, options = {}) {
  const headers = new Headers(options.headers);
  const token = sessionStorage.getItem('platform-access-token');
  if (token) headers.set('Authorization', `Bearer ${token}`);
  const response = await fetch(path, { ...options, headers });
  if (response.status === 401) {
    const notice = document.querySelector('#access-notice') || document.createElement('p');
    notice.id = 'access-notice'; notice.className = 'status';
    const link = document.createElement('a'); link.href = '/access.html'; link.textContent = 'Inicia sesión para continuar.';
    notice.replaceChildren(link); document.querySelector('main')?.prepend(notice);
  }
  return response;
};
window.addEventListener('DOMContentLoaded', async () => {
  try {
    const response = await fetch('/api/reconstruccion/access-mode');
    const mode = await response.json();
    if (mode.localDemo || mode.demoExternalData) {
      const notice = document.createElement('p'); notice.className = 'status'; notice.setAttribute('role', 'status');
      notice.textContent = 'Demostración local: los datos externos o la identidad son de prueba.';
      document.querySelector('main')?.prepend(notice);
    }
  } catch { /* Availability errors are shown by each screen's own request. */ }
});

window.attachmentReference = function (projectId, reference, label) {
  const item = document.createElement('li');
  if (!reference.startsWith('attachment:')) { item.textContent = reference; return item; }
  const button = document.createElement('button'); button.type = 'button'; button.textContent = label;
  button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      const response = await platformFetch(`/api/reconstruccion/proyectos/${projectId}/adjuntos/${reference.slice(11)}`);
      if (!response.ok) throw new Error('El archivo no está disponible.');
      const blob = await response.blob(); const url = URL.createObjectURL(blob);
      const link = document.createElement('a'); link.href = url;
      link.download = response.headers.get('Content-Disposition')?.match(/filename="?([^";]+)/)?.[1] || 'adjunto';
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 30000);
    } catch (error) { item.append(document.createTextNode(error.message)); }
    finally { button.disabled = false; }
  }); item.append(button); return item;
};
