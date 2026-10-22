# Extracted brand system

Source: `/Volumes/Project/NightTalesStudio/web/src/app/shared/ui/theme/_tokens.scss`, `_typography.scss`, and the seven supplied app PNGs.

A quiet green-gray production room, Vietnamese sans-serif typography, and a teal signal for the owner’s next decision.

```css
:root {
  --bg: oklch(25.867% 0.00812 169.696); /* #202523 */
  --surface: oklch(30.534% 0.01105 167.944); /* #2a312e */
  --fg: oklch(96.773% 0.00800 114.220); /* #f4f5ef */
  --muted: oklch(82.671% 0.01293 164.727); /* #bfc9c4 */
  --border: oklch(47.516% 0.01973 167.780); /* #52605a */
  --accent: oklch(77.924% 0.08961 186.406); /* #6ecac0 */
}
```

These OKLch equivalents document the dark source colors; `tokens.css` retains the actual source hex values with compatibility aliases and a matching light theme.

Display/body: Be Vietnam Pro, system-ui, sans-serif. Mono: ui-monospace, SF Mono, Menlo, Consolas, monospace. One UI family is appropriate for the utilitarian desk.

1. Dark green-gray room and lighter sheet separate workspace from controls.
2. Teal marks action; coral marks failure. Remove decorative repeated stripes.
3. Sentence-case Vietnamese labels and left-aligned content.
4. Existing compact controls and stepped workflow are retained in purpose, revised to 44px targets and less crowded spacing.
5. Borders organize rows; elevation is reserved for overlays.
