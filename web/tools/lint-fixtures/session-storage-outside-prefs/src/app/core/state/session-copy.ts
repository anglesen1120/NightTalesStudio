export function remember(value: string): void {
  sessionStorage.setItem('copy', value);
}
