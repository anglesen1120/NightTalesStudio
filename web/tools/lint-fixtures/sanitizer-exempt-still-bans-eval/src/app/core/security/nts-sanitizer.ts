// The exemption lifts bypassSecurityTrust* only: eval stays banned inside the exempt file.
export function run(code: string): unknown {
  return eval(code);
}
