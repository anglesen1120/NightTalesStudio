// QA defect: dynamic import() of the generated client outside data-access (no-restricted-imports ignores ImportExpression).
export const load = () => import('@shared/api');
