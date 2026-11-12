import { Button } from "@components/Button";
import { httpStatus } from "@fns/fetch-json";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { FormEvent, ReactNode, useId, useState } from "react";
import { COOLDOWN_MINUTES, MonitoringSettings, RETENTION_DAYS, monitoringKeys, saveSettings } from "./monitoring";

type Props = {
  appId: string;
  settings: MonitoringSettings;
};

type NumberField = "monitoringRetentionDays" | "alertCooldownMinutes";

const bounds: Record<NumberField, { min: number; max: number }> = {
  monitoringRetentionDays: RETENTION_DAYS,
  alertCooldownMinutes: COOLDOWN_MINUTES,
};

function validate(field: NumberField, value: string) {
  const { min, max } = bounds[field];
  const number = Number(value);
  if (value.trim() === "" || !Number.isInteger(number) || number < min || number > max) {
    return `Enter a whole number from ${min} to ${max}.`;
  }
  return null;
}

function saveErrorMessage(error: unknown) {
  switch (httpStatus(error)) {
    case 400:
      return "These settings were rejected. Check the values and try again.";
    case 403:
      return "Only the app owner can change monitoring settings.";
    default:
      return "Couldn't save the settings. Please try again.";
  }
}

export function MonitoringSettingsForm({ appId, settings }: Props) {
  const queryClient = useQueryClient();
  const [flags, setFlags] = useState({
    captureErrors: settings.captureErrors,
    capturePerformance: settings.capturePerformance,
    forwardRawErrors: settings.forwardRawErrors,
    localAlertsEnabled: settings.localAlertsEnabled,
  });
  const [numbers, setNumbers] = useState<Record<NumberField, string>>({
    monitoringRetentionDays: String(settings.monitoringRetentionDays),
    alertCooldownMinutes: String(settings.alertCooldownMinutes),
  });
  const [showErrors, setShowErrors] = useState(false);

  const mutation = useMutation({
    mutationFn: (next: MonitoringSettings) => saveSettings(appId, next),
    onSuccess: (saved) => {
      queryClient.setQueryData(monitoringKeys.settings(appId), saved);
      // Symbol expiry follows retention, and ready rows are no longer polled, so a shorter retention must refetch them.
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: monitoringKeys.data(appId) }),
        queryClient.invalidateQueries({ queryKey: monitoringKeys.allNativeSymbols(appId) }),
      ]);
    },
  });

  const errors = {
    monitoringRetentionDays: validate("monitoringRetentionDays", numbers.monitoringRetentionDays),
    alertCooldownMinutes: validate("alertCooldownMinutes", numbers.alertCooldownMinutes),
  };

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    setShowErrors(true);
    if (errors.monitoringRetentionDays || errors.alertCooldownMinutes) return;
    mutation.mutate({
      ...flags,
      monitoringRetentionDays: Number(numbers.monitoringRetentionDays),
      alertCooldownMinutes: Number(numbers.alertCooldownMinutes),
    });
  };

  const setFlag = (key: keyof typeof flags) => (checked: boolean) => {
    mutation.reset();
    setFlags((current) => ({ ...current, [key]: checked }));
  };

  const setNumber = (key: NumberField) => (value: string) => {
    mutation.reset();
    setNumbers((current) => ({ ...current, [key]: value }));
  };

  return (
    <form onSubmit={handleSubmit} noValidate className="space-y-6">
      <fieldset className="space-y-3">
        <legend className="text-sm font-semibold mb-2">Capture</legend>
        <Checkbox checked={flags.captureErrors} onChange={setFlag("captureErrors")} label="Capture errors">
          Accept new error reports for issues. When off, new reports and retries are rejected.
        </Checkbox>
        <Checkbox checked={flags.capturePerformance} onChange={setFlag("capturePerformance")} label="Capture performance">
          Accept new transactions and spans.
        </Checkbox>
        <Checkbox checked={flags.forwardRawErrors} onChange={setFlag("forwardRawErrors")} label="Forward raw error logs">
          Copy new error reports to the raw Error logs. Turning this off is not retroactive: raw logs already delivered
          are kept for 180 days.
        </Checkbox>
        <p className="text-xs text-muted-foreground">These apply to release and debug builds alike. Usage analytics are not affected.</p>
      </fieldset>

      <NumberInput
        label="Retention (days)"
        value={numbers.monitoringRetentionDays}
        onChange={setNumber("monitoringRetentionDays")}
        min={RETENTION_DAYS.min}
        max={RETENTION_DAYS.max}
        error={showErrors ? errors.monitoringRetentionDays : null}
      >
        How long error occurrence details and transactions are kept after they arrive, from {RETENTION_DAYS.min} to{" "}
        {RETENTION_DAYS.max} days. Issue summaries stay until you clear them. A shorter window also shortens how long
        retried reports are recognized as duplicates.
      </NumberInput>

      <fieldset className="space-y-3">
        <legend className="text-sm font-semibold mb-2">Local alerts</legend>
        <Checkbox checked={flags.localAlertsEnabled} onChange={setFlag("localAlertsEnabled")} label="Create local alerts">
          Add an entry to the Alerts inbox on the Errors page for new issues and for resolved issues that occur again.
          Nothing is emailed or sent outside this server.
        </Checkbox>
        <NumberInput
          label="Regression cooldown (minutes)"
          value={numbers.alertCooldownMinutes}
          onChange={setNumber("alertCooldownMinutes")}
          min={COOLDOWN_MINUTES.min}
          max={COOLDOWN_MINUTES.max}
          error={showErrors ? errors.alertCooldownMinutes : null}
        >
          Minimum time between alerts for the same issue, counted from that issue's last alert. A regression inside
          this window, including soon after its new-issue alert, creates no new alert.
        </NumberInput>
      </fieldset>

      <div className="flex flex-wrap items-center gap-3">
        <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
          {mutation.isPending ? "Saving…" : "Save settings"}
        </Button>
        {mutation.isSuccess && (
          <p role="status" className="text-sm text-success">
            Settings saved.
          </p>
        )}
        {mutation.isError && (
          <p role="alert" className="text-sm text-destructive">
            {saveErrorMessage(mutation.error)}
          </p>
        )}
      </div>
    </form>
  );
}

type CheckboxProps = {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  children: ReactNode;
};

function Checkbox({ checked, onChange, label, children }: CheckboxProps) {
  const id = useId();
  return (
    <div className="flex items-start gap-3">
      <input
        id={id}
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        aria-describedby={`${id}-hint`}
        className="mt-1 h-4 w-4 accent-primary focus-ring"
      />
      <div>
        <label htmlFor={id} className="text-sm font-medium">
          {label}
        </label>
        <p id={`${id}-hint`} className="text-sm text-muted-foreground">
          {children}
        </p>
      </div>
    </div>
  );
}

type NumberInputProps = {
  label: string;
  value: string;
  onChange: (value: string) => void;
  min: number;
  max: number;
  error: string | null;
  children: ReactNode;
};

function NumberInput({ label, value, onChange, min, max, error, children }: NumberInputProps) {
  const id = useId();
  return (
    <div className="space-y-1">
      <label htmlFor={id} className="text-sm font-medium block">
        {label}
      </label>
      <input
        id={id}
        type="number"
        inputMode="numeric"
        min={min}
        max={max}
        step={1}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        aria-invalid={!!error}
        aria-describedby={`${id}-hint${error ? ` ${id}-error` : ""}`}
        className="h-9 w-28 rounded-md border border-input bg-background px-2 text-sm ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 aria-[invalid=true]:border-destructive"
      />
      {error && (
        <p id={`${id}-error`} className="text-sm text-destructive">
          {error}
        </p>
      )}
      <p id={`${id}-hint`} className="text-sm text-muted-foreground max-w-xl">
        {children}
      </p>
    </div>
  );
}
