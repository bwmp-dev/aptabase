import { LabelledSelect } from "@components/LabelledSelect";
import { BuildMode } from "@features/apps";
import { retryServerErrors } from "@fns/fetch-json";
import { useQuery } from "@tanstack/react-query";
import { ReleaseFilter, fetchReleases, releaseKeys } from "./releases";

type Props = {
  appId: string;
  buildMode: BuildMode;
  value: ReleaseFilter;
  onChange: (changes: Partial<ReleaseFilter>) => void;
};

function uniqueSorted(values: string[], selected: string) {
  const unique = new Set(values.filter(Boolean));
  if (selected) unique.add(selected);
  return [...unique].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
}

// Options come from the observed release catalog; a selected value stays listed even if it has aged out.
export function ReleaseFilters({ appId, buildMode, value, onChange }: Props) {
  const { data } = useQuery({
    queryKey: releaseKeys.list(appId, buildMode),
    queryFn: () => fetchReleases(appId, buildMode),
    retry: retryServerErrors,
  });
  const releases = data?.releases ?? [];

  return (
    <>
      <LabelledSelect
        label="Release"
        allLabel="All releases"
        value={value.release}
        options={uniqueSorted(
          releases.map((r) => r.release),
          value.release
        ).reverse()}
        onChange={(release) => onChange({ release })}
      />
      <LabelledSelect
        label="Environment"
        allLabel="All environments"
        value={value.environment}
        options={uniqueSorted(
          releases.map((r) => r.environment),
          value.environment
        )}
        onChange={(environment) => onChange({ environment })}
      />
    </>
  );
}
