import { ErrorState } from "@components/ErrorState";
import { LoadingState } from "@components/LoadingState";
import { Page, PageHeading } from "@components/Page";
import { BuildMode, useApps, useCurrentApp } from "@features/apps";
import { httpStatus, retryServerErrors } from "@fns/fetch-json";
import { useQuery } from "@tanstack/react-query";
import { Link, Navigate } from "react-router-dom";
import { BuildModeSelector } from "../analytics/mode/BuildModeSelector";
import { ObservedRelease, fetchReleases, releaseKeys } from "./releases";

Component.displayName = "ReleasesPage";
export function Component() {
  const app = useCurrentApp();
  const { buildMode } = useApps();

  if (!app) return <Navigate to="/" />;

  return (
    <Page title="Releases">
      <div className="flex justify-between items-center">
        <PageHeading title="Releases" subtitle="Releases and environments your app has reported" />
        <BuildModeSelector />
      </div>
      <ReleaseCatalog appId={app.id} buildMode={buildMode} />
    </Page>
  );
}

export function ReleaseCatalog({ appId, buildMode }: { appId: string; buildMode: BuildMode }) {
  const { data, error, isLoading, isError, refetch } = useQuery({
    queryKey: releaseKeys.list(appId, buildMode),
    queryFn: () => fetchReleases(appId, buildMode),
    retry: retryServerErrors,
  });

  const renderBody = () => {
    if (isLoading) {
      return (
        <div className="py-12">
          <LoadingState size="lg" />
        </div>
      );
    }
    if (isError || !data) {
      return httpStatus(error) === 403 ? (
        <p role="alert" className="py-12 text-center text-muted-foreground">
          You don't have access to this app's releases.
        </p>
      ) : (
        <div className="py-12">
          <ErrorState refetch={refetch} />
        </div>
      );
    }
    if (data.releases.length === 0) {
      return (
        <div role="status" className="bg-muted rounded py-12 px-4 text-center space-y-1">
          <p className="text-lg">No releases reported yet</p>
          <p className="text-sm text-muted-foreground">
            Send a release label with error reports or transactions from {buildMode} builds to see it here.
          </p>
        </div>
      );
    }
    return <ReleaseTable appId={appId} releases={data.releases} />;
  };

  return (
    <div className="mt-6 space-y-4">
      <p className="text-sm text-muted-foreground max-w-3xl">
        Built from error reports and sampled transactions still retained from {buildMode} builds (kept for up to 30 days, depending on app retention),
        most recently seen first (up to 100). This is not a deployment history.
      </p>
      {renderBody()}
    </div>
  );
}

function filterLink(appId: string, page: "errors" | "performance", release: ObservedRelease) {
  const params = new URLSearchParams({ release: release.release });
  if (release.environment) params.set("environment", release.environment);
  return `/${appId}/${page}?${params}`;
}

function ReleaseTable({ appId, releases }: { appId: string; releases: ObservedRelease[] }) {
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full divide-y divide-gray-300">
        <caption className="sr-only">Observed releases, most recently seen first</caption>
        <thead>
          <tr>
            <th scope="col" className="py-3 pl-4 pr-3 text-left text-sm font-semibold">
              Release
            </th>
            <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
              Errors
            </th>
            <th scope="col" className="px-3 py-3 text-right text-sm font-semibold">
              Transactions
            </th>
            <th scope="col" className="hidden md:table-cell px-3 py-3 text-left text-sm font-semibold">
              First seen
            </th>
            <th scope="col" className="hidden sm:table-cell px-3 py-3 text-left text-sm font-semibold">
              Last seen
            </th>
            <th scope="col" className="px-3 py-3 text-left text-sm font-semibold">
              <span className="sr-only">Links</span>
            </th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-600">
          {releases.map((release) => {
            const label = [release.release, release.environment].filter(Boolean).join(" ");
            return (
              <tr key={`${release.release}\n${release.environment}`}>
                <td className="py-3 pl-4 pr-3 text-sm max-w-[10rem] sm:max-w-xs">
                  <span className="block font-medium truncate">{release.release}</span>
                  <span className="block text-muted-foreground truncate">
                    {release.environment || "No environment"}
                  </span>
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {release.errorCount.toLocaleString()}
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-sm text-right tabular-nums">
                  {release.transactionCount.toLocaleString()}
                </td>
                <td className="hidden md:table-cell whitespace-nowrap px-3 py-3 text-sm">
                  <time dateTime={release.firstSeen}>{new Date(release.firstSeen).toLocaleString()}</time>
                </td>
                <td className="hidden sm:table-cell whitespace-nowrap px-3 py-3 text-sm">
                  <time dateTime={release.lastSeen}>{new Date(release.lastSeen).toLocaleString()}</time>
                </td>
                <td className="px-3 py-3 text-sm">
                  <div className="flex flex-col gap-1 sm:flex-row sm:gap-3">
                    <Link className="underline underline-offset-2 hover:text-primary" to={filterLink(appId, "errors", release)}>
                      Issues<span className="sr-only"> for {label}</span>
                    </Link>
                    <Link
                      className="underline underline-offset-2 hover:text-primary"
                      to={filterLink(appId, "performance", release)}
                    >
                      Performance<span className="sr-only"> for {label}</span>
                    </Link>
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
