import { twJoin } from "tailwind-merge";

type Props<T extends string> = {
  label: string;
  options: readonly { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
};

export function SegmentedControl<T extends string>({ label, options, value, onChange }: Props<T>) {
  return (
    <div role="group" aria-label={label} className="inline-flex items-center rounded-md bg-muted p-1 text-muted-foreground">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-pressed={value === option.value}
          onClick={() => onChange(option.value)}
          className={twJoin(
            "rounded-sm px-3 py-1.5 text-sm font-medium whitespace-nowrap transition-colors focus-ring",
            value === option.value ? "bg-background text-foreground shadow-sm" : "hover:text-foreground"
          )}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}
