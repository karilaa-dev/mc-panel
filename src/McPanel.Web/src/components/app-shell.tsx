import { Suspense, useCallback, useEffect, useRef, useState, type ComponentType } from "react"
import { Link, Outlet, useLocation } from "react-router-dom"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Breadcrumbs, Button, DropdownMenu, LinkProvider, Sidebar, useSidebar } from "@cloudflare/kumo"
import {
  ArchiveIcon, CalendarCheckIcon, CaretUpDownIcon, CoffeeIcon, CpuIcon, DesktopIcon, FolderSimpleIcon, GaugeIcon,
  GearSixIcon, HardDrivesIcon, ImageIcon, ListIcon, MoonIcon, PackageIcon, PlayIcon, PlugIcon, PlusIcon, PulseIcon,
  ArrowClockwiseIcon, ShareNetworkIcon, SignOutIcon, SignpostIcon, SlidersHorizontalIcon, SquaresFourIcon, SunIcon,
  TerminalWindowIcon, UserCircleIcon, UsersIcon,
} from "@phosphor-icons/react"
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

const dashboardItem: NavigationItem = { label: "Dashboard", path: "/", icon: SquaresFourIcon }
const serverItems: NavigationItem[] = [
  { label: "Overview", path: "", icon: GaugeIcon },
  { label: "Console", path: "/console", icon: TerminalWindowIcon },
  { label: "Backends", path: "/backends", icon: SignpostIcon, gateOnly: true },
  { label: "Gate settings", path: "/gate", icon: ShareNetworkIcon, gateOnly: true },
  { label: "Server properties", path: "/properties", icon: SlidersHorizontalIcon, minecraftOnly: true },
  { label: "Server icon", path: "/icon", icon: ImageIcon, minecraftOnly: true },
  { label: "Runtime", path: "/runtime", icon: CpuIcon, minecraftOnly: true },
  { label: "Mods", path: "/mods", icon: PackageIcon, moddedOnly: true, minecraftOnly: true },
  { label: "Plugins", path: "/plugins", icon: PlugIcon, paperOnly: true, minecraftOnly: true },
  { label: "Files", path: "/files", icon: FolderSimpleIcon },
  { label: "Players", path: "/players", icon: UsersIcon, minecraftOnly: true },
  { label: "Backups", path: "/backups", icon: ArchiveIcon, minecraftOnly: true },
  { label: "Schedules", path: "/schedules", icon: CalendarCheckIcon, minecraftOnly: true },
]
const systemItems: NavigationItem[] = [
  { label: "Activity", path: "/activity", icon: PulseIcon },
  { label: "Java", path: "/java", icon: CoffeeIcon },
  { label: "Panel settings", path: "/panel-settings", icon: GearSixIcon },
]
const sidebarStorageKey = "sidebar-collapsed"

function isModded(server?: ServerSummaryDto) {
  return Boolean(server && (["Fabric", "Forge", "NeoForge"].includes(server.kind) || server.modpack))
}

function NavigationLink({ item, href }: { item: NavigationItem; href: string }) {
  const { pathname } = useLocation()
  const active = href === "/" ? pathname === "/" : pathname === href
  return <Sidebar.MenuButton icon={item.icon} href={href} active={active} tooltip={item.label} aria-current={active ? "page" : undefined}>{item.label}</Sidebar.MenuButton>
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
    { value: "system", label: "System", icon: DesktopIcon },
    { value: "light", label: "Light", icon: SunIcon },
    { value: "dark", label: "Dark", icon: MoonIcon },
  ] as const
  return (
    <DropdownMenu>
      <DropdownMenu.Trigger render={
        <button type="button" className="flex h-8.5 min-w-0 flex-1 cursor-pointer items-center gap-2 rounded-lg px-2 text-sm font-medium text-kumo-default hover:bg-kumo-tint focus-visible:ring-2 focus-visible:ring-kumo-brand focus-visible:outline-none data-[popup-open]:bg-kumo-tint" />
      }>
        <UserCircleIcon size={18} className="shrink-0 text-kumo-subtle" />
        <span className="truncate">Administrator</span>
        <CaretUpDownIcon size={14} className="ml-auto shrink-0 text-kumo-subtle" />
      </DropdownMenu.Trigger>
      <DropdownMenu.Content side="top" align="end" className="min-w-52">
        <DropdownMenu.Group>
          <DropdownMenu.Label>Theme</DropdownMenu.Label>
          {themes.map((option) => <DropdownMenu.Item key={option.value} icon={option.icon} selected={theme === option.value} onClick={() => setTheme(option.value)}>{option.label}</DropdownMenu.Item>)}
        </DropdownMenu.Group>
        <DropdownMenu.Separator />
        <DropdownMenu.Item icon={SignOutIcon} variant="danger" onClick={() => void logout()}>Log out</DropdownMenu.Item>
      </DropdownMenu.Content>
    </DropdownMenu>
  )
}

function AppSidebar({ servers, currentServer, serverId }: { servers: ServerSummaryDto[]; currentServer?: ServerSummaryDto; serverId?: string }) {
  const visibleServerItems = serverItems.filter((item) =>
    (!item.gateOnly || currentServer?.kind === "Gate") &&
    (!item.minecraftOnly || currentServer?.kind !== "Gate") &&
    (!item.moddedOnly || isModded(currentServer)) &&
    (!item.paperOnly || currentServer?.kind === "Paper"))
  const { pathname } = useLocation()
  const { state, setOpen, isMobile, setOpenMobile } = useSidebar()
  const [serversOpen, setServersOpen] = useState(true)

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
        {serverId && (
          <Sidebar.Group>
            <Sidebar.GroupLabel>
              <span className="flex min-w-0 items-center gap-2">
                {currentServer && <ServerAvatar server={currentServer} className="size-4 rounded-[4px] ring-0" />}
                <span className="truncate">{currentServer?.name ?? "Server"}</span>
              </span>
            </Sidebar.GroupLabel>
            <Sidebar.Menu>
              {visibleServerItems.map((item) => <NavigationLink key={item.label} item={item} href={`/servers/${serverId}${item.path}`} />)}
            </Sidebar.Menu>
          </Sidebar.Group>
        )}
        <Sidebar.Group>
          <Sidebar.Menu>
            <Sidebar.MenuItem>
              <Sidebar.Collapsible open={serversOpen} onOpenChange={(next) => {
                if (state === "collapsed") { setOpen(true); setServersOpen(true) }
                else setServersOpen(next)
              }}>
                <Sidebar.CollapsibleTrigger render={<Sidebar.MenuButton icon={HardDrivesIcon} tooltip="Servers">Servers<Sidebar.MenuChevron /></Sidebar.MenuButton>} />
                <Sidebar.CollapsibleContent>
                  <Sidebar.MenuSub>
                    {servers.map((server) => {
                      const href = `/servers/${server.id}`
                      return (
                        <Sidebar.MenuSubButton key={server.id} href={href} active={pathname === href} aria-current={pathname === href ? "page" : undefined}>
                          <ServerAvatar server={server} className="size-4 rounded-[4px] ring-0" />
                          <span className="truncate">{server.name}</span>
                          <span aria-hidden="true" className={`ml-auto size-1.5 shrink-0 rounded-full ${server.state === "Running" ? "bg-kumo-success" : server.state === "Crashed" || server.state === "Error" ? "bg-kumo-danger" : server.state === "Stopped" ? "bg-kumo-interact" : "bg-kumo-info"}`} />
                        </Sidebar.MenuSubButton>
                      )
                    })}
                    <Sidebar.MenuSubButton href="/create" active={pathname === "/create"}>
                      <PlusIcon size={14} className="shrink-0 text-kumo-subtle" />
                      <span className="truncate">Create server</span>
                    </Sidebar.MenuSubButton>
                  </Sidebar.MenuSub>
                </Sidebar.CollapsibleContent>
              </Sidebar.Collapsible>
            </Sidebar.MenuItem>
          </Sidebar.Menu>
        </Sidebar.Group>
        <Sidebar.Group>
          <Sidebar.GroupLabel>System</Sidebar.GroupLabel>
          <Sidebar.Menu>{systemItems.map((item) => <NavigationLink key={item.path} item={item} href={item.path} />)}</Sidebar.Menu>
        </Sidebar.Group>
      </Sidebar.Content>
      <Sidebar.Footer className="gap-1 px-[11px] group-not-data-[state=collapsed]/sidebar:px-2.5">
        <Sidebar.Trigger />
        <AccountMenu />
      </Sidebar.Footer>
    </Sidebar>
  )
}

function ServerHeaderActions({ server, onOverview }: { server: ServerSummaryDto; onOverview: boolean }) {
  const lifecycle = useServerLifecycle(server)
  const pending = lifecycle.pendingAction
  return (
    <div className="flex shrink-0 items-center gap-2">
      <span className="hidden sm:inline-flex"><StatusBadge state={server.state} /></span>
      {!onOverview && pending && <Button size="sm" loading>{pendingLifecycleLabels[pending]}</Button>}
      {!onOverview && !pending && server.state === "Running" && <Button size="sm" icon={ArrowClockwiseIcon} disabled={lifecycle.busy} onClick={() => lifecycle.run("restart")}>Restart</Button>}
      {!onOverview && !pending && (server.state === "Stopped" || server.state === "Crashed") && <Button size="sm" variant="primary" icon={PlayIcon} disabled={lifecycle.busy} onClick={() => lifecycle.run("start")}>Start</Button>}
      {!onOverview && <ImmediateStopButton server={server} size="sm" />}
    </div>
  )
}

function AppHeader({ currentServer, serverId }: { currentServer?: ServerSummaryDto; serverId?: string }) {
  const { pathname } = useLocation()
  const { isMobile, setOpenMobile } = useSidebar()
  const serverSection = serverId ? serverItems.find((item) => pathname === `/servers/${serverId}${item.path}`) : undefined
  const pageName = /^\/servers\/[^/]+\/creating\/[^/]+$/.test(pathname) ? "Creating server"
    : serverSection?.label
      ?? [dashboardItem, { label: "Create server", path: "/create", icon: PlusIcon }, ...systemItems].find((item) => item.path === pathname)?.label
      ?? "MC Panel"
  const onOverview = Boolean(serverId && pathname === `/servers/${serverId}`)

  return (
    <header className="sticky top-0 z-10 flex h-14 shrink-0 items-center gap-3 border-b border-kumo-line bg-kumo-canvas/85 px-4 backdrop-blur-md sm:px-6 lg:px-10">
      {isMobile && <Button variant="ghost" shape="square" size="sm" icon={<ListIcon size={18} />} aria-label="Open navigation" onClick={() => setOpenMobile(true)} />}
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
      {currentServer && <ServerHeaderActions server={currentServer} onOverview={onOverview} />}
    </header>
  )
}

export function AppShell() {
  const location = useLocation()
  const serverId = location.pathname.match(/^\/servers\/([^/]+)/)?.[1]
  const { data: servers = [] } = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 5_000 })
  const currentServer = servers.find((server) => server.id === serverId)
  const [defaultOpen] = useState(() => localStorage.getItem(sidebarStorageKey) !== "true")
  const persistSidebar = useCallback((open: boolean) => localStorage.setItem(sidebarStorageKey, String(!open)), [])

  return (
    <LinkProvider component={RouterLink}>
      <Sidebar.Provider defaultOpen={defaultOpen} collapsible="icon" onOpenChange={persistSidebar}>
        <MobileSidebarCloser />
        <AppSidebar servers={servers} currentServer={currentServer} serverId={serverId} />
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
