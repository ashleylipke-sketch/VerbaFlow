import { createRoot } from 'react-dom/client';
import App from './App';
import { finishMicrosoftPopup, isMicrosoftPopupReturn, startSignIn } from './auth';
import './styles.css';

const root = () => document.getElementById('root')!;
if (isMicrosoftPopupReturn()) {
  // Microsoft's permission window: give its answer to the VerbaFlow page that opened it, never start the app here.
  finishMicrosoftPopup()
    .catch(() => undefined)
    .finally(() => { root().textContent = 'Done. You can close this window and go back to VerbaFlow.'; });
} else {
  // A Microsoft sign-in the browser has just come back from is finished before the app reads the page address.
  startSignIn().finally(() => createRoot(root()).render(<App />));
}
