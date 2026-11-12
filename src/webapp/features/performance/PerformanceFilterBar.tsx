import { SegmentedControl } from "@components/SegmentedControl";
import { BuildMode } from "@features/apps";
import { ReleaseFilters } from "@features/releases/ReleaseFilters";
import { Period, PerformanceFilters, periods } from "./performance";

type Props = {
  appId: string;
  buildMode: BuildMode;
  filters: PerformanceFilters;
  onChange: (changes: Partial<PerformanceFilters>) => void;
};

export function PerformanceFilterBar({ appId, buildMode, filters, onChange }: Props) {
  return (
    <div className="flex flex-wrap items-end gap-x-4 gap-y-3">
      <SegmentedControl
        label="Time period"
        options={periods}
        value={filters.period}
        onChange={(period: Period) => onChange({ period })}
      />
      <ReleaseFilters appId={appId} buildMode={buildMode} value={filters} onChange={onChange} />
    </div>
  );
}
