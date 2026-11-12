// Synthetic source-map fixture. tsc erases these type declarations, so the
// compiled line of the throw differs from its original line below.
interface SyntheticSettings {
  readonly label: string;
  readonly retries: number;
}

type SyntheticResult = { ok: false; reason: string };

export function loadSyntheticSettings(settings: SyntheticSettings): SyntheticResult {
  if (settings.retries >= 0) {
    throw new Error(`Node probe ${settings.label}`); // SOURCE-MAP-PROBE-THROW
  }
  return { ok: false, reason: 'unreachable' };
}
