const button = document.querySelector('#copy-build');
const commands = document.querySelector('#build-commands');
const status = document.querySelector('#copy-status');
if (button && commands && status && navigator.clipboard && window.isSecureContext) {
  button.hidden = false;
  button.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(commands.textContent.trim());
      button.textContent = 'Copied';
      status.textContent = 'Build commands copied to your clipboard.';
    } catch {
      button.textContent = 'Select the commands';
      status.textContent = 'Copy was unavailable. Select and copy the commands above.';
    }
  });
}
