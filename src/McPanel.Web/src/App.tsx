import { lazy, useEffect } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { createBrowserRouter, RouterProvider, Navigate, Route, Routes } from "react-router-dom"
import { Button, Loader, Toasty, TooltipProvider } from "@cloudflare/kumo"
import { WifiSlashIcon } from "@phosphor-icons/react"
import { AuthScreen } from "@/components/auth-screen"
import { AppShell } from "@/components/app-shell"
import { EmptyState } from "@/components/feedback"
import { errorMessage } from "@/lib/errors"
import { api } from "@/lib/api"
import { toastManager } from "@/lib/toast"

const pageModules = {
  activity: () => import("@/pages/activity-page"),
  core: () => import("@/pages/core-pages"),
  mods: () => import("@/pages/mods-page"),
  operations: () => import("@/pages/operations-pages"),
  management: () => import("@/pages/management-pages"),
  gateProxy: () => import("@/pages/gate-proxy-page"),
  gateBackends: () => import("@/pages/gate-backends-page"),
}

const ActivityPage = lazy(() => pageModules.activity().then((module) => ({ default: module.ActivityPage })))
const DashboardPage = lazy(() => pageModules.core().then((module) => ({ default: module.DashboardPage })))
const CreateServerPage = lazy(() => pageModules.core().then((module) => ({ default: module.CreateServerPage })))
const ServerCreationPage = lazy(() => pageModules.core().then((module) => ({ default: module.ServerCreationPage })))
const ServerOverviewPage = lazy(() => pageModules.core().then((module) => ({ default: module.ServerOverviewPage })))
const ServerPropertiesPage = lazy(() => pageModules.core().then((module) => ({ default: module.ServerPropertiesPage })))
const ServerIconPage = lazy(() => pageModules.core().then((module) => ({ default: module.ServerIconPage })))
const RuntimeSettingsPage = lazy(() => pageModules.core().then((module) => ({ default: module.RuntimeSettingsPage })))
const ModsPage = lazy(() => pageModules.mods().then((module) => ({ default: module.ModsPage })))
const PluginsPage = lazy(() => pageModules.mods().then((module) => ({ default: module.PluginsPage })))
const ConsolePage = lazy(() => pageModules.operations().then((module) => ({ default: module.ConsolePage })))
const FilesPage = lazy(() => pageModules.operations().then((module) => ({ default: module.FilesPage })))
const PlayersPage = lazy(() => pageModules.operations().then((module) => ({ default: module.PlayersPage })))
const BackupsPage = lazy(() => pageModules.operations().then((module) => ({ default: module.BackupsPage })))
const SchedulesPage = lazy(() => pageModules.management().then((module) => ({ default: module.SchedulesPage })))
const JavaPage = lazy(() => pageModules.management().then((module) => ({ default: module.JavaPage })))
const PanelSettingsPage = lazy(() => pageModules.management().then((module) => ({ default: module.PanelSettingsPage })))
const GateProxyPage = lazy(() => pageModules.gateProxy().then((module) => ({ default: module.GateProxyPage })))
const GateBackendsPage = lazy(() => pageModules.gateBackends().then((module) => ({ default: module.GateBackendsPage })))

/** Warm every page chunk once the browser is idle so later navigation never waits on code. */
function usePreloadPages(enabled: boolean) {
  useEffect(() => {
    if (!enabled) return
    const load = () => { for (const load of Object.values(pageModules)) void load().catch(() => undefined) }
    if ("requestIdleCallback" in window) {
      const handle = window.requestIdleCallback(load, { timeout: 4_000 })
      return () => window.cancelIdleCallback(handle)
    }
    const handle = globalThis.setTimeout(load, 1_500)
    return () => globalThis.clearTimeout(handle)
  }, [enabled])
}

function LoadingScreen() {
  return (
    <main className="delayed-appear flex min-h-svh flex-col items-center justify-center gap-4 p-6">
      <img src="/mcpanel.svg" alt="" className="size-12" />
      <Loader size="sm" className="text-kumo-subtle" aria-label="Loading MC Panel" />
    </main>
  )
}

function UnavailableScreen({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <main className="flex min-h-svh items-center justify-center p-6">
      <EmptyState
        className="max-w-lg"
        icon={<WifiSlashIcon />}
        title="Panel unavailable"
        description={message}
        action={<Button variant="primary" onClick={onRetry}>Try again</Button>}
      />
    </main>
  )
}

function AppRoutes() {
  const queryClient = useQueryClient()
  useEffect(() => {
    const expired = () => { queryClient.setQueryData(["auth-status"], { authenticated: false, setupRequired: false }); queryClient.removeQueries({ predicate: query => query.queryKey[0] !== "auth-status" }); void queryClient.invalidateQueries({ queryKey: ["auth-status"] }) }
    window.addEventListener("mcpanel-session-expired", expired)
    return () => window.removeEventListener("mcpanel-session-expired", expired)
  }, [queryClient])
  const auth = useQuery({
    queryKey: ["auth-status"],
    queryFn: api.authStatus,
    retry: false,
    staleTime: 30_000,
    refetchInterval: 30_000,
  })
  usePreloadPages(Boolean(auth.data?.authenticated))

  if (auth.isLoading) return <LoadingScreen />
  // A failed background refresh keeps the app on screen; only a failed first load blocks it.
  if (!auth.data) return <UnavailableScreen message={errorMessage(auth.error, "Could not reach MC Panel.")} onRetry={() => void auth.refetch()} />
  if (!auth.data.authenticated) return <AuthScreen status={auth.data} />

  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<DashboardPage />} />
        <Route path="activity" element={<ActivityPage />} />
        <Route path="create" element={<CreateServerPage />} />
        <Route path="servers/:serverId/creating/:jobId" element={<ServerCreationPage />} />
        <Route path="servers/:serverId" element={<ServerOverviewPage />} />
        <Route path="servers/:serverId/console" element={<ConsolePage />} />
        <Route path="servers/:serverId/backends" element={<GateBackendsPage />} />
        <Route path="servers/:serverId/gate" element={<GateProxyPage />} />
        <Route path="servers/:serverId/properties" element={<ServerPropertiesPage />} />
        <Route path="servers/:serverId/icon" element={<ServerIconPage />} />
        <Route path="servers/:serverId/runtime" element={<RuntimeSettingsPage />} />
        <Route path="servers/:serverId/mods" element={<ModsPage />} />
        <Route path="servers/:serverId/plugins" element={<PluginsPage />} />
        <Route path="servers/:serverId/files" element={<FilesPage />} />
        <Route path="servers/:serverId/players" element={<PlayersPage />} />
        <Route path="servers/:serverId/backups" element={<BackupsPage />} />
        <Route path="servers/:serverId/schedules" element={<SchedulesPage />} />
        <Route path="java" element={<JavaPage />} />
        <Route path="panel-settings" element={<PanelSettingsPage />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  )
}

const router = createBrowserRouter([{ path: "*", element: <AppRoutes /> }])

export function App() {
  return (
    <Toasty toastManager={toastManager}>
      <TooltipProvider>
        <RouterProvider router={router} />
      </TooltipProvider>
    </Toasty>
  )
}

export default App
