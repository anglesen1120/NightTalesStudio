/** Why a session ended; drives the banner copy `core.session.<reason>.*` (rendered by 10.3.3-T1). */
export type SessionEndReason = 'logout' | 'expired' | 'revoked' | 'reused' | 'restore_failed';
