export function stageKey(stage: string | null | undefined): string | null {
  switch (stage) {
    case 'queued':
    case 'done':
      return `episodes.generation.stage.${stage}`;
    case 'writing':
    case 'checking':
    case 'reviewing':
    case 'revising':
    case 'scoring':
    case 'saving':
      return `story.generation.stage.${stage}`;
    default:
      return null;
  }
}

export function isTerminal(status: string | null | undefined): boolean {
  return status === 'succeeded' || status === 'failed' || status === 'cancelled';
}

export function failureKey(code: string | null | undefined): string | null {
  if (!code) return null;
  switch (code) {
    case 'story_moderated':
      return 'story.generation.moderation.brief';
    case 'story_invalid_output':
    case 'story_generation_failed':
    case 'spend_cap_reached':
      return `story.generation.error.${code}`;
    case 'story_generation_limit':
      // Run failures have no Retry-After; application resolves errors.story_generation_limit without {{seconds}}.
      return null;
    default:
      // Core's error catalogue belongs to application; null asks that layer to resolve its fallback copy.
      return null;
  }
}
