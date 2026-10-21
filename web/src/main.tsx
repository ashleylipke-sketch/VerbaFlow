import { createRoot } from 'react-dom/client';
import App from './App';
import { startSignIn } from './auth';
import './styles.css';
// A Microsoft sign-in the browser has just come back from is finished before the app reads the page address.
startSignIn().finally(() => createRoot(document.getElementById('root')!).render(<App />));
