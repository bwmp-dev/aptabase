import { SegmentedControl } from "@components/SegmentedControl";
import { BuildMode } from "@features/apps";
import { ReleaseFilters } from "@features/releases/ReleaseFilters";
import { ReleaseFilter } from "@features/releases/releases";
import { Period, PeriodFilters, periods } from "./performance";

type Props = {
  appId: string;
  buildMode: BuildMode;
  filters: PeriodFilters;
  onChange: (changes: Partial<PeriodFilters>) => void;
  observed?: ReleaseFilter[];
};

export function PerformanceFilterBar({ appId, buildMode, filters, onChange, observed }: Props) {
  return (
    <div className="flex flex-wrap items-end gap-x-4 gap-y-3">
      <SegmentedControl
        label="Time period"
        options={periods}
        value={filters.period}
        onChange={(period: Period) => onChange({ period })}
      />
      <ReleaseFilters appId={appId} buildMode={buildMode} value={filters} onChange={onChange} observed={observed} />
    </div>
  );
}
