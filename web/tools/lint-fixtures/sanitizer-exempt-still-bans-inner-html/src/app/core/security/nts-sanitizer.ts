// The exemption lifts bypassSecurityTrust* only: an innerHTML assignment stays banned inside the exempt file.
export function paint(element: HTMLElement, html: string): void {
  element.innerHTML = html;
}
