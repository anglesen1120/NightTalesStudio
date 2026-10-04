export function readLanguage(): string | null {
  return localStorage.getItem('language');
}
