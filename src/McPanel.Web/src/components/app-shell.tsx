import { Suspense, useCallback, useEffect, useRef, useState, type ComponentType } from "react"
import { Link, Outlet, useLocation } from "react-router-dom"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Breadcrumbs, Button, DropdownMenu, LinkProvider, Sidebar, SkeletonLine, cn, useSidebar } from "@cloudflare/kumo"
import { ActivityIcon, AdjustmentsHorizontalIcon, ArchiveIcon, CalendarCheckIcon, CoffeeIcon, CpuIcon, DeviceDesktopIcon, FolderIcon, GaugeIcon, LayoutDashboardIcon, LogoutIcon, MenuIcon, MoonIcon, PackageIcon, PhotoIcon, PlayerPlayIcon, PlugIcon, PlusIcon, RefreshIcon, SelectorIcon, SettingsIcon, ShareIcon, SignRightIcon, SunIcon, Terminal2Icon, UserCircleIcon, UsersIcon } from "@/components/icons"
import { api } from "@/lib/api"
import type { ServerSummaryDto } from "@/lib/contracts"
import { toast } from "@/lib/toast"
import { useTheme } from "@/components/theme-provider"
import { StatusBadge } from "@/components/status-badge"
import { ServerAvatar } from "@/components/server-icon"
import { ImmediateStopButton } from "@/components/immediate-stop-button"
import { RouterLink } from "@/components/router-link"
import { pendingLifecycleLabels, useServerLifecycle } from "@/hooks/use-server-lifecycle"
import { PageFallback } from "@/components/page-fallback"
import { contentFrame } from "@/components/page"

type IconComponent = ComponentType<{ className?: string }>
type NavigationItem = {
  label: string
  path: string
  icon: IconComponent
  gateOnly?: boolean
  minecraftOnly?: boolean
  moddedOnly?: boolean
  paperOnly?: boolean
}

const dashboardItem: NavigationItem = { label: "Dashboard", path: "/", icon: LayoutDashboardIcon }
const createItem: NavigationItem = { label: "Create server", path: "/create", icon: PlusIcon }
const serverItems: NavigationItem[] = [
  // Pages every server has come first, so they hold the same rows on every server type.
  { label: "Overview", path: "", icon: GaugeIcon },
  { label: "Console", path: "/console", icon: Terminal2Icon },
  { label: "Files", path: "/files", icon: FolderIcon },
  { label: "Backends", path: "/backends", icon: SignRightIcon, gateOnly: true },
  { label: "Gate settings", path: "/gate", icon: ShareIcon, gateOnly: true },
  { label: "Players", path: "/players", icon: UsersIcon, minecraftOnly: true },
  { label: "Backups", path: "/backups", icon: ArchiveIcon, minecraftOnly: true },
  { label: "Schedules", path: "/schedules", icon: CalendarCheckIcon, minecraftOnly: true },
  { label: "Server properties", path: "/properties", icon: AdjustmentsHorizontalIcon, minecraftOnly: true },
  { label: "Server icon", path: "/icon", icon: PhotoIcon, minecraftOnly: true },
  { label: "Runtime", path: "/runtime", icon: CpuIcon, minecraftOnly: true },
  // Loader-specific pages go last, where their presence never moves the rows above.
  { label: "Mods", path: "/mods", icon: PackageIcon, moddedOnly: true, minecraftOnly: true },
  { label: "Plugins", path: "/plugins", icon: PlugIcon, paperOnly: true, minecraftOnly: true },
]
const systemItems: NavigationItem[] = [
  { label: "Activity", path: "/activity", icon: ActivityIcon },
  { label: "Java", path: "/java", icon: CoffeeIcon },
  { label: "Panel settings", path: "/panel-settings", icon: SettingsIcon },
]
const sidebarStorageKey = "sidebar-collapsed"

function isModded(server?: ServerSummaryDto) {
  return Boolean(server && (["Fabric", "Forge", "NeoForge"].includes(server.kind) || server.modpack))
}

/** The pages a server offers. Until its type is known, only the pages every server has. */
function serverNavigation(server?: ServerSummaryDto) {
  if (!server) return serverItems.filter((item) => !item.gateOnly && !item.minecraftOnly)
  return serverItems.filter((item) =>
    (!item.gateOnly || server.kind === "Gate") &&
    (!item.minecraftOnly || server.kind !== "Gate") &&
    (!item.moddedOnly || isModded(server)) &&
    (!item.paperOnly || server.kind === "Paper"))
}

function statusTone(state: ServerSummaryDto["state"]) {
  if (state === "Running") return "bg-kumo-success"
  if (state === "Crashed" || state === "Error") return "bg-kumo-danger"
  if (state === "Stopped") return "bg-kumo-interact"
  return "bg-kumo-info"
}

/** Kumo draws sidebar icons at 16px and 40% opacity, which leaves Tabler's padded glyphs faint beside the labels. */
const navigationIcon = "size-4.5 shrink-0 text-kumo-subtle transition-colors group-hover/menu-button:text-kumo-default group-data-[active]/menu-button:text-kumo-default"

function NavigationLink({ item, href }: { item: NavigationItem; href: string }) {
  const { pathname } = useLocation()
  const active = href === "/" ? pathname === "/" : pathname === href
  return (
    <Sidebar.MenuButton icon={<item.icon className={navigationIcon} />} href={href} itemId={href} active={active} tooltip={item.label} aria-current={active ? "page" : undefined}>
      {item.label}
    </Sidebar.MenuButton>
  )
}

/** A roster row. The status dot sits on the avatar so it stays put when the sidebar collapses to icons. */
function ServerLink({ server, selected }: { server: ServerSummaryDto; selected: boolean }) {
  const { pathname } = useLocation()
  const href = `/servers/${server.id}`
  const avatar = (
    <span className="relative flex size-4.5 shrink-0">
      <ServerAvatar server={server} className="size-full rounded-[5px]" />
      <span
        aria-hidden="true"
        className={cn(
          "absolute -right-[3px] -bottom-[3px] size-2 rounded-full ring-2 ring-(--sidebar-bg)",
          "group-hover/menu-button:ring-(--sidebar-active-bg) group-focus-visible/menu-button:ring-(--sidebar-active-bg) group-data-[active]/menu-button:ring-(--sidebar-active-bg)",
          statusTone(server.state),
        )}
      />
    </span>
  )
  return (
    <Sidebar.MenuButton icon={avatar} href={href} active={selected} tooltip={`${server.name} · ${server.state}`} aria-current={pathname === href ? "page" : undefined}>
      {server.name}
    </Sidebar.MenuButton>
  )
}

/** Holds the roster's place while the first server list loads. */
function ServerRowsPlaceholder() {
  return Array.from({ length: 2 }, (_, index) => (
    <li key={index} aria-hidden="true" className="delayed-appear flex min-h-8.5 items-center gap-3 px-3 group-data-[state=collapsed]/sidebar:justify-center group-data-[state=collapsed]/sidebar:px-0">
      <SkeletonLine className="size-4.5 shrink-0 rounded-[5px]" />
      <SkeletonLine className="h-2.5 w-24 rounded-full group-data-[state=collapsed]/sidebar:hidden" />
    </li>
  ))
}

/** Closes the mobile navigation sheet whenever the route changes. */
function MobileSidebarCloser() {
  const sidebar = useSidebar()
  const { pathname } = useLocation()
  const latest = useRef(sidebar)
  useEffect(() => { latest.current = sidebar })
  useEffect(() => {
    if (latest.current.isMobile) latest.current.setOpenMobile(false)
  }, [pathname])
  return null
}

function AccountMenu() {
  const queryClient = useQueryClient()
  const { theme, setTheme } = useTheme()
  async function logout() {
    try {
      await api.logout()
      queryClient.clear()
      window.location.assign("/")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not log out.")
    }
  }
  const themes = [
    { value: "system", label: "System", icon: DeviceDesktopIcon },
    { value: "light", label: "Light", icon: SunIcon },
    { value: "dark", label: "Dark", icon: MoonIcon },
  ] as const
  return (
    <DropdownMenu>
      <DropdownMenu.Trigger render={
        <button type="button" className="flex h-8.5 min-w-0 flex-1 cursor-pointer items-center gap-2 rounded-lg px-2 text-sm font-medium text-kumo-default hover:bg-kumo-tint focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none data-[popup-open]:bg-kumo-tint" />
      }>
        <UserCircleIcon size={20} className="shrink-0 text-kumo-subtle" />
        <span className="truncate">Administrator</span>
        <SelectorIcon size={16} className="ml-auto shrink-0 text-kumo-subtle" />
      </DropdownMenu.Trigger>
      <DropdownMenu.Content side="top" align="end" className="min-w-52">
        <DropdownMenu.Group>
          <DropdownMenu.Label>Theme</DropdownMenu.Label>
          {themes.map((option) => <DropdownMenu.Item key={option.value} icon={option.icon} selected={theme === option.value} onClick={() => setTheme(option.value)}>{option.label}</DropdownMenu.Item>)}
        </DropdownMenu.Group>
        <DropdownMenu.Separator />
        <DropdownMenu.Item icon={LogoutIcon} variant="danger" onClick={() => void logout()}>Log out</DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu>
  )
}

/**
 * Three fixed zones: the roster under Dashboard, the selected server's pages below it, and the
 * system links pinned above the footer. Opening a server fills space below the roster, so nothing
 * the pointer is on moves.
 */
function AppSidebar({ servers, serversLoading, currentServer, serverId }: { servers: ServerSummaryDto[]; serversLoading: boolean; currentServer?: ServerSummaryDto; serverId?: string }) {
  const { pathname } = useLocation()
  const { isMobile, setOpenMobile, scrollItemIntoView } = useSidebar()
  // On a short screen the selected server's pages can sit below the fold; bring the current one into view.
  // Type-specific pages only mount once the server's kind is known, so scroll again when it arrives.
  const serverKind = currentServer?.kind
  useEffect(() => { scrollItemIntoView(pathname) }, [pathname, serverKind, scrollItemIntoView])

  return (
    <Sidebar
      aria-label="Application sidebar"
      className="md:sticky md:top-0 md:h-svh"
      // Any link closes the mobile sheet, including one to the current page.
      onClick={(event) => { if (isMobile && (event.target as Element).closest("a[href]")) setOpenMobile(false) }}
    >
      <Sidebar.Header className="gap-2.5 px-3.5">
        <Link to="/" className="flex min-w-0 items-center gap-2.5 rounded-lg text-kumo-strong no-underline focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none">
          <img src="/mcpanel.svg" alt="" className="size-7 shrink-0" />
          <span className="truncate text-base font-semibold">MC Panel</span>
        </Link>
      </Sidebar.Header>
      <Sidebar.Content>
        <Sidebar.Group>
          <Sidebar.Menu><NavigationLink item={dashboardItem} href="/" /></Sidebar.Menu>
        </Sidebar.Group>
        <Sidebar.Group>
          <Sidebar.GroupLabel>Servers</Sidebar.GroupLabel>
          <Sidebar.Menu>
            {serversLoading ? <ServerRowsPlaceholder /> : servers.map((server) => <ServerLink key={server.id} server={server} selected={server.id === serverId} />)}
            <NavigationLink item={createItem} href={createItem.path} />
          </Sidebar.Menu>
        </Sidebar.Group>
        {serverId && (
          <Sidebar.Group>
            <Sidebar.GroupLabel>
              {currentServer?.name ?? (serversLoading ? <SkeletonLine className="my-[5px] h-2.5 w-24 rounded-full" /> : "Server")}
            </Sidebar.GroupLabel>
            <Sidebar.Menu>
              {serverNavigation(currentServer).map((item) => <NavigationLink key={item.label} item={item} href={`/servers/${serverId}${item.path}`} />)}
            </Sidebar.Menu>
          </Sidebar.Group>
        )}
      </Sidebar.Content>
      <div className="shrink-0 border-t border-kumo-line px-[11px] py-2 transition-[padding] duration-(--sidebar-animation-duration) group-not-data-[state=collapsed]/sidebar:px-3.5">
        <Sidebar.Menu>{systemItems.map((item) => <NavigationLink key={item.path} item={item} href={item.path} />)}</Sidebar.Menu>
      </div>
      <Sidebar.Footer className="gap-1 px-[11px] group-not-data-[state=collapsed]/sidebar:px-2.5">
        <Sidebar.Trigger />
        <AccountMenu />
      </Sidebar.Footer>
    </Sidebar>
  )
}

/** Lifecycle shortcuts for server sub-pages. On phones they collapse to icon buttons so the breadcrumb keeps its room. */
function ServerHeaderActions({ server, onOverview, compact }: { server: ServerSummaryDto; onOverview: boolean; compact: boolean }) {
  const lifecycle = useServerLifecycle(server)
  const pending = lifecycle.pendingAction
  const restart = () => lifecycle.run("restart")
  const start = () => lifecycle.run("start")
  return (
    <div className="flex shrink-0 items-center gap-2">
      <span className="hidden sm:inline-flex"><StatusBadge state={server.state} /></span>
      {!onOverview && pending && (compact
        ? <Button size="sm" shape="square" loading aria-label={pendingLifecycleLabels[pending]} />
        : <Button size="sm" loading>{pendingLifecycleLabels[pending]}</Button>)}
      {!onOverview && !pending && server.state === "Running" && (compact
        ? <Button size="sm" shape="square" icon={RefreshIcon} aria-label="Restart" disabled={lifecycle.busy} onClick={restart} />
        : <Button size="sm" icon={RefreshIcon} disabled={lifecycle.busy} onClick={restart}>Restart</Button>)}
      {!onOverview && !pending && (server.state === "Stopped" || server.state === "Crashed") && (compact
        ? <Button size="sm" shape="square" variant="primary" icon={PlayerPlayIcon} aria-label="Start" disabled={lifecycle.busy} onClick={start} />
        : <Button size="sm" variant="primary" icon={PlayerPlayIcon} disabled={lifecycle.busy} onClick={start}>Start</Button>)}
      {!onOverview && <ImmediateStopButton server={server} size="sm" compact={compact} />}
    </div>
  )
}

function AppHeader({ currentServer, serverId }: { currentServer?: ServerSummaryDto; serverId?: string }) {
  const { pathname } = useLocation()
  const { isMobile, setOpenMobile } = useSidebar()
  const serverSection = serverId ? serverItems.find((item) => pathname === `/servers/${serverId}${item.path}`) : undefined
  const pageName = /^\/servers\/[^/]+\/creating\/[^/]+$/.test(pathname) ? "Creating server"
    : serverSection?.label
      ?? [dashboardItem, createItem, ...systemItems].find((item) => item.path === pathname)?.label
      ?? "MC Panel"
  const onOverview = Boolean(serverId && pathname === `/servers/${serverId}`)

  return (
    <header className="sticky top-0 z-10 shrink-0 border-b border-kumo-line bg-kumo-canvas/85 backdrop-blur-md">
      <div className={cn(contentFrame, "flex h-14 items-center gap-3")}>
        {isMobile && <Button variant="ghost" shape="square" size="sm" className="-ml-1" icon={<MenuIcon size={20} />} aria-label="Open navigation" onClick={() => setOpenMobile(true)} />}
        <Breadcrumbs className="min-w-0 flex-1">
          {currentServer && pathname !== `/servers/${currentServer.id}` ? <>
            <Breadcrumbs.Link href={`/servers/${currentServer.id}`} icon={<ServerAvatar server={currentServer} className="size-4 rounded-[4px] ring-0" />}>
              <span className="max-w-48 truncate">{currentServer.name}</span>
            </Breadcrumbs.Link>
            <Breadcrumbs.Separator />
          </> : currentServer ? <>
            <Breadcrumbs.Link href="/">Dashboard</Breadcrumbs.Link>
            <Breadcrumbs.Separator />
          </> : null}
          <Breadcrumbs.Current icon={currentServer && onOverview ? <ServerAvatar server={currentServer} className="size-4 rounded-[4px] ring-0" /> : undefined}>
            {onOverview && currentServer ? currentServer.name : pageName}
          </Breadcrumbs.Current>
        </Breadcrumbs>
        {currentServer && <ServerHeaderActions server={currentServer} onOverview={onOverview} compact={isMobile} />}
      </div>
    </header>
  )
}

export function AppShell() {
  const location = useLocation()
  const serverId = location.pathname.match(/^\/servers\/([^/]+)/)?.[1]
  const { data: servers = [], isPending: serversLoading } = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 5_000 })
  const currentServer = servers.find((server) => server.id === serverId)
  const [defaultOpen] = useState(() => localStorage.getItem(sidebarStorageKey) !== "true")
  const persistSidebar = useCallback((open: boolean) => localStorage.setItem(sidebarStorageKey, String(!open)), [])

  return (
    <LinkProvider component={RouterLink}>
      <Sidebar.Provider defaultOpen={defaultOpen} collapsible="icon" onOpenChange={persistSidebar}>
        <MobileSidebarCloser />
        <AppSidebar servers={servers} serversLoading={serversLoading} currentServer={currentServer} serverId={serverId} />
        <div className="flex min-h-svh min-w-0 flex-1 flex-col bg-kumo-canvas">
          <AppHeader currentServer={currentServer} serverId={serverId} />
          <main className="flex min-w-0 flex-1 flex-col">
            <Suspense fallback={<PageFallback />}><Outlet /></Suspense>
          </main>
        </div>
      </Sidebar.Provider>
    </LinkProvider>
  )
}
