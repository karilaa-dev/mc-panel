import { lazy, Suspense } from "react"
import { useQuery } from "@tanstack/react-query"
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom"
import { AuthScreen } from "@/components/auth-screen"
import { AppShell } from "@/components/app-shell"
import { Empty, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from "@/components/ui/empty"
import { Skeleton } from "@/components/ui/skeleton"
import { Toaster } from "@/components/ui/sonner"
import { WifiOffIcon } from "lucide-react"
import { api } from "@/lib/api"

const DashboardPage = lazy(() => import("@/pages/core-pages").then((module) => ({ default: module.DashboardPage })))
const CreateServerPage = lazy(() => import("@/pages/core-pages").then((module) => ({ default: module.CreateServerPage })))
const ServerCreationPage = lazy(() => import("@/pages/core-pages").then((module) => ({ default: module.ServerCreationPage })))
const ServerOverviewPage = lazy(() => import("@/pages/core-pages").then((module) => ({ default: module.ServerOverviewPage })))
const ServerPropertiesPage = lazy(() => import("@/pages/core-pages").then((module) => ({ default: module.ServerPropertiesPage })))
const ServerIconPage = lazy(() => import("@/pages/core-pages").then((module) => ({ default: module.ServerIconPage })))
const RuntimeSettingsPage = lazy(() => import("@/pages/core-pages").then((module) => ({ default: module.RuntimeSettingsPage })))
const ModsPage = lazy(() => import("@/pages/mods-page").then((module) => ({ default: module.ModsPage })))
const PluginsPage = lazy(() => import("@/pages/mods-page").then((module) => ({ default: module.PluginsPage })))
const ConsolePage = lazy(() => import("@/pages/operations-pages").then((module) => ({ default: module.ConsolePage })))
const FilesPage = lazy(() => import("@/pages/operations-pages").then((module) => ({ default: module.FilesPage })))
const PlayersPage = lazy(() => import("@/pages/operations-pages").then((module) => ({ default: module.PlayersPage })))
const BackupsPage = lazy(() => import("@/pages/operations-pages").then((module) => ({ default: module.BackupsPage })))
const SchedulesPage = lazy(() => import("@/pages/management-pages").then((module) => ({ default: module.SchedulesPage })))
const JavaPage = lazy(() => import("@/pages/management-pages").then((module) => ({ default: module.JavaPage })))
const PanelSettingsPage = lazy(() => import("@/pages/management-pages").then((module) => ({ default: module.PanelSettingsPage })))
const GateProxyPage = lazy(() => import("@/pages/gate-proxy-page").then((module) => ({ default: module.GateProxyPage })))
const GateBackendsPage = lazy(() => import("@/pages/gate-backends-page").then((module) => ({ default: module.GateBackendsPage })))

function LoadingScreen() {
  return (
    <main className="mx-auto flex min-h-svh w-full max-w-6xl flex-col gap-6 p-6">
      <Skeleton className="h-12 w-48" />
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {Array.from({ length: 4 }).map((_, index) => (
          <Skeleton key={index} className="h-28" />
        ))}
      </div>
      <Skeleton className="h-80" />
    </main>
  )
}

function UnavailableScreen({ message }: { message: string }) {
  return (
    <main className="flex min-h-svh items-center justify-center p-6">
      <Empty>
        <EmptyHeader>
          <EmptyMedia variant="icon"><WifiOffIcon /></EmptyMedia>
          <EmptyTitle>Panel unavailable</EmptyTitle>
          <EmptyDescription>{message}</EmptyDescription>
        </EmptyHeader>
      </Empty>
    </main>
  )
}

export function LegacySettingsRedirect() {
  return <Navigate to="../properties" replace relative="path" />
}

export function LegacySoftwareRedirect() {
  return <Navigate to="../runtime" replace relative="path" />
}

function AppRoutes() {
  const auth = useQuery({
    queryKey: ["auth-status"],
    queryFn: api.authStatus,
    retry: false,
    staleTime: 30_000,
  })

  if (auth.isLoading) return <LoadingScreen />
  if (auth.isError) {
    return <UnavailableScreen message={auth.error instanceof Error ? auth.error.message : "Could not reach MC Panel."} />
  }
  if (!auth.data?.authenticated) return <AuthScreen status={auth.data!} />

  return (
    <Suspense fallback={<LoadingScreen />}>
      <Routes>
      <Route element={<AppShell />}>
        <Route index element={<DashboardPage />} />
        <Route path="create" element={<CreateServerPage />} />
        <Route path="servers/:serverId/creating/:jobId" element={<ServerCreationPage />} />
        <Route path="servers/:serverId" element={<ServerOverviewPage />} />
        <Route path="servers/:serverId/console" element={<ConsolePage />} />
        <Route path="servers/:serverId/backends" element={<GateBackendsPage />} />
        <Route path="servers/:serverId/gate" element={<GateProxyPage />} />
        <Route path="servers/:serverId/properties" element={<ServerPropertiesPage />} />
        <Route path="servers/:serverId/icon" element={<ServerIconPage />} />
        <Route path="servers/:serverId/runtime" element={<RuntimeSettingsPage />} />
        <Route path="servers/:serverId/software" element={<LegacySoftwareRedirect />} />
        <Route path="servers/:serverId/mods" element={<ModsPage />} />
        <Route path="servers/:serverId/plugins" element={<PluginsPage />} />
        <Route path="servers/:serverId/settings" element={<LegacySettingsRedirect />} />
        <Route path="servers/:serverId/files" element={<FilesPage />} />
        <Route path="servers/:serverId/players" element={<PlayersPage />} />
        <Route path="servers/:serverId/backups" element={<BackupsPage />} />
        <Route path="servers/:serverId/schedules" element={<SchedulesPage />} />
        <Route path="java" element={<JavaPage />} />
        <Route path="panel-settings" element={<PanelSettingsPage />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
      </Routes>
    </Suspense>
  )
}

export function App() {
  return (
    <BrowserRouter>
      <AppRoutes />
      <Toaster richColors closeButton />
    </BrowserRouter>
  )
}

export default App
