/** Base URL for the API when not using Vite proxy (no trailing slash). Example: http://localhost:5088 */
interface ImportMetaEnv {
  readonly VITE_API_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
