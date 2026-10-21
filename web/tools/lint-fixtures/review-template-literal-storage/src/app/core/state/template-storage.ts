// Review F-3b: template-literal computed storage access.
export const read = (): string | null => window[`localStorage`].getItem('k');
