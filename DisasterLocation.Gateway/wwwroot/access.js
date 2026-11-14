document.querySelector('#access-form').addEventListener('submit', async event => {
  event.preventDefault();
  const token = document.querySelector('#token').value.trim();
  const response = await fetch('/api/reconstruccion/proyectos', { headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) { document.querySelector('#access-status').textContent = 'No se pudo validar el acceso o faltan permisos.'; return; }
  sessionStorage.setItem('platform-access-token', token);
  location.assign('/reconstruction.html');
});
document.querySelector('#logout').addEventListener('click', () => {
  sessionStorage.removeItem('platform-access-token'); document.querySelector('#token').value = '';
  document.querySelector('#access-status').textContent = 'Sesión cerrada en este navegador.';
});
