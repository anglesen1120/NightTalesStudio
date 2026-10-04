// QA defect: dynamic import() deep into another feature (lazy loading is the idiomatic way to cross a boundary).
export const load = () => import('@features/episodes/ui/episode-card');
