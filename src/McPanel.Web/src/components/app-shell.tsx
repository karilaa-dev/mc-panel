import { useEffect, useMemo } from "react"
import { Link, Outlet, useLocation, useNavigate } from "react-router-dom"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import {
  ArchiveIcon, BlocksIcon, BoxIcon, ChevronUpIcon, CircleGaugeIcon, PlusIcon,
  Clock3Icon, CommandIcon, CpuIcon, FileIcon, LogOutIcon, MonitorCogIcon, MoonIcon,
  PanelsTopLeftIcon, Settings2Icon, SunIcon, TerminalSquareIcon, UsersIcon,
} from "lucide-react"
import { api } from "@/lib/api"
import { useTheme } from "@/components/theme-provider"
import { StatusBadge } from "@/components/status-badge"
import { Button } from "@/components/ui/button"
import {
  Breadcrumb, BreadcrumbItem, BreadcrumbList, BreadcrumbPage, BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel,
  DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import {
  Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select"
import { Separator } from "@/components/ui/separator"
import {
  Sidebar, SidebarContent, SidebarFooter, SidebarGroup, SidebarGroupContent,
  SidebarGroupLabel, SidebarHeader, SidebarInset, SidebarMenu, SidebarMenuButton,
  SidebarMenuItem, SidebarProvider, SidebarRail, SidebarTrigger,
  useSidebar,
} from "@/components/ui/sidebar"
import { toast } from "sonner"

const mainItems = [
  { label: "Dashboard", path: "/", icon: PanelsTopLeftIcon },
  { label: "Create server", path: "/create", icon: PlusIcon },
]
const serverItems = [
  { label: "Overview", path: "", icon: CircleGaugeIcon },
  { label: "Console", path: "/console", icon: TerminalSquareIcon },
  { label: "Settings", path: "/settings", icon: Settings2Icon },
  { label: "Files", path: "/files", icon: FileIcon },
  { label: "Players", path: "/players", icon: UsersIcon },
  { label: "Backups", path: "/backups", icon: ArchiveIcon },
  { label: "Schedules", path: "/schedules", icon: Clock3Icon },
]
const systemItems = [
  { label: "Java", path: "/java", icon: BlocksIcon },
  { label: "Panel Settings", path: "/panel-settings", icon: MonitorCogIcon },
]

function NavigationItem({ label, path, icon: Icon }: { label: string; path: string; icon: typeof BoxIcon }) {
  const location = useLocation()
  const { isMobile, setOpenMobile } = useSidebar()
  const active = path === "/" ? location.pathname === "/" : location.pathname === path
  return (
    <SidebarMenuItem>
      <SidebarMenuButton
        tooltip={label}
        isActive={active}
        render={<Link to={path} onClick={() => isMobile && setOpenMobile(false)} />}
      >
        <Icon />
        <span>{label}</span>
      </SidebarMenuButton>
    </SidebarMenuItem>
  )
}

export function MobileSidebarCloser() {
  const { isMobile, setOpenMobile } = useSidebar()
  const { pathname } = useLocation()

  useEffect(() => {
    if (isMobile) setOpenMobile(false)
  }, [isMobile, pathname, setOpenMobile])

  return null
}

export function AppShell() {
  const location = useLocation()
  const serverId = location.pathname.match(/^\/servers\/([^/]+)/)?.[1]
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { theme, setTheme } = useTheme()
  const { data: servers = [] } = useQuery({ queryKey: ["servers"], queryFn: api.servers, refetchInterval: 5_000 })
  const lifecycle = useMutation({
    mutationFn: ({ id, action }: { id: string; action: "start" | "restart" }) => api.lifecycle(id, action),
    onSuccess: (job, { id }) => {
      toast.success("Operation started", { description: job.message ?? `Operation ${job.id.slice(0, 8)}` })
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
      void queryClient.invalidateQueries({ queryKey: ["server", id] })
    },
    onError: (error) => toast.error(error.message),
  })
  const currentServer = servers.find((server) => server.id === serverId)
  const serverOptions = useMemo(() => servers.map((server) => ({ value: server.id, label: server.name })), [servers])
  const serverSection = serverItems.find((item) =>
    location.pathname === `/servers/${serverId}${item.path}`,
  )
  const pageName = serverSection?.label
    ?? [...mainItems, ...systemItems].find((item) => item.path === location.pathname)?.label
    ?? "MC Panel"

  async function logout() {
    try {
      await api.logout()
      queryClient.clear()
      window.location.assign("/")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not log out.")
    }
  }

  return (
    <SidebarProvider>
      <MobileSidebarCloser />
      <Sidebar variant="inset" collapsible="icon">
        <SidebarHeader>
          <SidebarMenu>
            <SidebarMenuItem>
              <SidebarMenuButton size="lg" render={<Link to="/" />} tooltip="MC Panel">
                <span className="flex size-8 items-center justify-center rounded-lg bg-primary text-primary-foreground"><CommandIcon /></span>
                <span className="grid flex-1 text-left text-sm leading-tight"><span className="truncate font-semibold">MC Panel</span><span className="truncate text-xs text-muted-foreground">Minecraft, simply managed</span></span>
              </SidebarMenuButton>
            </SidebarMenuItem>
          </SidebarMenu>
          {servers.length > 0 && (
            <Select
              items={serverOptions}
              value={serverId ?? null}
              onValueChange={(value) => value && navigate(`/servers/${value}`)}
            >
              <SelectTrigger className="w-full group-data-[collapsible=icon]:hidden" aria-label="Active server">
                <CpuIcon />
                <SelectValue placeholder="Select a server" />
              </SelectTrigger>
              <SelectContent>
                <SelectGroup>
                  {serverOptions.map((item) => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
                </SelectGroup>
              </SelectContent>
            </Select>
          )}
        </SidebarHeader>
        <SidebarContent>
          <SidebarGroup>
            <SidebarGroupLabel>Main</SidebarGroupLabel>
            <SidebarGroupContent><SidebarMenu>{mainItems.map((item) => <NavigationItem key={item.path} {...item} />)}</SidebarMenu></SidebarGroupContent>
          </SidebarGroup>
          {serverId && (
            <SidebarGroup>
              <SidebarGroupLabel>{currentServer?.name ?? "Active server"}</SidebarGroupLabel>
              <SidebarGroupContent>
                <SidebarMenu>{serverItems.map((item) => <NavigationItem key={item.label} label={item.label} icon={item.icon} path={`/servers/${serverId}${item.path}`} />)}</SidebarMenu>
              </SidebarGroupContent>
            </SidebarGroup>
          )}
          <SidebarGroup>
            <SidebarGroupLabel>System</SidebarGroupLabel>
            <SidebarGroupContent><SidebarMenu>{systemItems.map((item) => <NavigationItem key={item.path} {...item} />)}</SidebarMenu></SidebarGroupContent>
          </SidebarGroup>
        </SidebarContent>
        <SidebarFooter>
          <SidebarMenu>
            <SidebarMenuItem>
              <DropdownMenu>
                <DropdownMenuTrigger render={<SidebarMenuButton title="Theme and account" />}>
                  {theme === "dark" ? <MoonIcon /> : <SunIcon />}
                  <span className="truncate">Administrator</span>
                  <ChevronUpIcon className="ml-auto" />
                </DropdownMenuTrigger>
                <DropdownMenuContent side="top" align="start" className="min-w-48">
                  <DropdownMenuGroup>
                    <DropdownMenuLabel>Theme</DropdownMenuLabel>
                    {(["system", "light", "dark"] as const).map((value) => <DropdownMenuItem key={value} onClick={() => setTheme(value)}>{value === "dark" ? <MoonIcon /> : <SunIcon />}{value[0].toUpperCase() + value.slice(1)}</DropdownMenuItem>)}
                  </DropdownMenuGroup>
                  <DropdownMenuSeparator />
                  <DropdownMenuGroup><DropdownMenuItem onClick={logout}><LogOutIcon />Log out</DropdownMenuItem></DropdownMenuGroup>
                </DropdownMenuContent>
              </DropdownMenu>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarFooter>
        <SidebarRail />
      </Sidebar>
      <SidebarInset>
        <header className="sticky top-0 flex h-14 shrink-0 items-center gap-3 border-b bg-background/95 px-4 backdrop-blur md:px-6">
          <SidebarTrigger />
          <Separator orientation="vertical" className="h-4" />
          <Breadcrumb className="min-w-0 flex-1">
            <BreadcrumbList>
              {currentServer && <><BreadcrumbItem className="hidden sm:inline-flex">{currentServer.name}</BreadcrumbItem><BreadcrumbSeparator className="hidden sm:inline-flex" /></>}
              <BreadcrumbItem><BreadcrumbPage>{pageName}</BreadcrumbPage></BreadcrumbItem>
            </BreadcrumbList>
          </Breadcrumb>
          {currentServer && <div className="hidden sm:block"><StatusBadge state={currentServer.state} /></div>}
          {currentServer && currentServer.state === "Running" && <Button size="sm" variant="outline" disabled={lifecycle.isPending} onClick={() => lifecycle.mutate({ id: currentServer.id, action: "restart" })}>{lifecycle.isPending ? "Restarting…" : "Restart"}</Button>}
          {currentServer && currentServer.state === "Stopped" && <Button size="sm" disabled={lifecycle.isPending} onClick={() => lifecycle.mutate({ id: currentServer.id, action: "start" })}>{lifecycle.isPending ? "Starting…" : "Start"}</Button>}
        </header>
        <Outlet />
      </SidebarInset>
    </SidebarProvider>
  )
}
