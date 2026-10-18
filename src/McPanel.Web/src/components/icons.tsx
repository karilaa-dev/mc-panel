import { forwardRef } from "react"
import {
  IconActivity, IconAdjustmentsHorizontal, IconAlertCircle, IconAlertCircleFilled, IconAlertTriangle,
  IconAlertTriangleFilled, IconAlignLeft, IconArchive, IconArrowBackUp, IconArrowDown, IconArrowRight,
  IconArrowsExchange, IconArrowsLeftRight, IconArrowUp, IconBan, IconBold, IconBoltOff, IconBraces,
  IconCalendarCheck, IconCheck, IconChevronDown, IconChevronRight, IconCircleCheck, IconCircleCheckFilled,
  IconCloudUpload, IconCoffee, IconCopy, IconCpu, IconCube, IconDatabase, IconDeviceDesktop, IconDice5,
  IconDoorExit, IconDots, IconDownload, IconEraser, IconExternalLink, IconEye, IconEyeOff, IconFile,
  IconFileExport, IconFilePlus, IconFileZip, IconFolder, IconFolderFilled, IconFolderPlus, IconGauge,
  IconHeart, IconHistory, IconHome, IconInfoCircle, IconInfoCircleFilled, IconItalic, IconKey,
  IconLayoutDashboard, IconLayoutGrid, IconListCheck, IconLogout, IconMemory, IconMenu2, IconMoon,
  IconPackage, IconPalette, IconPencil, IconPhoto, IconPin, IconPinnedOff, IconPlayerPlay, IconPlayerStop,
  IconPlug, IconPlus, IconRefresh, IconRotate, IconSearch, IconSelector, IconSend, IconServer, IconSettings,
  IconShare, IconShieldCheck, IconSignRight, IconSparkles, IconStrikethrough, IconSun, IconTerminal2,
  IconTrash, IconUnderline, IconUpload, IconUserCheck, IconUserCircle, IconUserMinus, IconUsers,
  IconWifiOff, IconX, type IconProps, type TablerIcon,
} from "@tabler/icons-react"

/** One stroke weight for every outline icon; at 16px it matches the 1px strokes of Kumo's built-in icons. */
const ICON_STROKE = 1.5

/**
 * Wraps a Tabler icon so it sizes to the surrounding text (1em) by default. Kumo renders icon
 * components passed to Button, InputGroup, and similar slots without a size, so they follow the
 * control's font size; explicit `size` and `stroke` props still win.
 */
function appIcon(Tabler: TablerIcon) {
  const AppIcon = forwardRef<SVGSVGElement, IconProps>(function AppIcon(props, ref) {
    return <Tabler size="1em" stroke={ICON_STROKE} {...props} ref={ref} />
  })
  AppIcon.displayName = Tabler.displayName
  return AppIcon
}

export const ActivityIcon = /* @__PURE__ */ appIcon(IconActivity)
export const AdjustmentsHorizontalIcon = /* @__PURE__ */ appIcon(IconAdjustmentsHorizontal)
export const AlertCircleIcon = /* @__PURE__ */ appIcon(IconAlertCircle)
export const AlertCircleFilledIcon = /* @__PURE__ */ appIcon(IconAlertCircleFilled)
export const AlertTriangleIcon = /* @__PURE__ */ appIcon(IconAlertTriangle)
export const AlertTriangleFilledIcon = /* @__PURE__ */ appIcon(IconAlertTriangleFilled)
export const AlignLeftIcon = /* @__PURE__ */ appIcon(IconAlignLeft)
export const ArchiveIcon = /* @__PURE__ */ appIcon(IconArchive)
export const ArrowBackUpIcon = /* @__PURE__ */ appIcon(IconArrowBackUp)
export const ArrowDownIcon = /* @__PURE__ */ appIcon(IconArrowDown)
export const ArrowRightIcon = /* @__PURE__ */ appIcon(IconArrowRight)
export const ArrowsExchangeIcon = /* @__PURE__ */ appIcon(IconArrowsExchange)
export const ArrowsLeftRightIcon = /* @__PURE__ */ appIcon(IconArrowsLeftRight)
export const ArrowUpIcon = /* @__PURE__ */ appIcon(IconArrowUp)
export const BanIcon = /* @__PURE__ */ appIcon(IconBan)
export const BoldIcon = /* @__PURE__ */ appIcon(IconBold)
export const BoltOffIcon = /* @__PURE__ */ appIcon(IconBoltOff)
export const BracesIcon = /* @__PURE__ */ appIcon(IconBraces)
export const CalendarCheckIcon = /* @__PURE__ */ appIcon(IconCalendarCheck)
export const CheckIcon = /* @__PURE__ */ appIcon(IconCheck)
export const ChevronDownIcon = /* @__PURE__ */ appIcon(IconChevronDown)
export const ChevronRightIcon = /* @__PURE__ */ appIcon(IconChevronRight)
export const CircleCheckIcon = /* @__PURE__ */ appIcon(IconCircleCheck)
export const CircleCheckFilledIcon = /* @__PURE__ */ appIcon(IconCircleCheckFilled)
export const CloudUploadIcon = /* @__PURE__ */ appIcon(IconCloudUpload)
export const CoffeeIcon = /* @__PURE__ */ appIcon(IconCoffee)
export const CopyIcon = /* @__PURE__ */ appIcon(IconCopy)
export const CpuIcon = /* @__PURE__ */ appIcon(IconCpu)
export const CubeIcon = /* @__PURE__ */ appIcon(IconCube)
export const DatabaseIcon = /* @__PURE__ */ appIcon(IconDatabase)
export const DeviceDesktopIcon = /* @__PURE__ */ appIcon(IconDeviceDesktop)
export const Dice5Icon = /* @__PURE__ */ appIcon(IconDice5)
export const DoorExitIcon = /* @__PURE__ */ appIcon(IconDoorExit)
export const DotsIcon = /* @__PURE__ */ appIcon(IconDots)
export const DownloadIcon = /* @__PURE__ */ appIcon(IconDownload)
export const EraserIcon = /* @__PURE__ */ appIcon(IconEraser)
export const ExternalLinkIcon = /* @__PURE__ */ appIcon(IconExternalLink)
export const EyeIcon = /* @__PURE__ */ appIcon(IconEye)
export const EyeOffIcon = /* @__PURE__ */ appIcon(IconEyeOff)
export const FileIcon = /* @__PURE__ */ appIcon(IconFile)
export const FileExportIcon = /* @__PURE__ */ appIcon(IconFileExport)
export const FilePlusIcon = /* @__PURE__ */ appIcon(IconFilePlus)
export const FileZipIcon = /* @__PURE__ */ appIcon(IconFileZip)
export const FolderIcon = /* @__PURE__ */ appIcon(IconFolder)
export const FolderFilledIcon = /* @__PURE__ */ appIcon(IconFolderFilled)
export const FolderPlusIcon = /* @__PURE__ */ appIcon(IconFolderPlus)
export const GaugeIcon = /* @__PURE__ */ appIcon(IconGauge)
export const HeartIcon = /* @__PURE__ */ appIcon(IconHeart)
export const HistoryIcon = /* @__PURE__ */ appIcon(IconHistory)
export const HomeIcon = /* @__PURE__ */ appIcon(IconHome)
export const InfoCircleIcon = /* @__PURE__ */ appIcon(IconInfoCircle)
export const InfoCircleFilledIcon = /* @__PURE__ */ appIcon(IconInfoCircleFilled)
export const ItalicIcon = /* @__PURE__ */ appIcon(IconItalic)
export const KeyIcon = /* @__PURE__ */ appIcon(IconKey)
export const LayoutDashboardIcon = /* @__PURE__ */ appIcon(IconLayoutDashboard)
export const LayoutGridIcon = /* @__PURE__ */ appIcon(IconLayoutGrid)
export const ListCheckIcon = /* @__PURE__ */ appIcon(IconListCheck)
export const LogoutIcon = /* @__PURE__ */ appIcon(IconLogout)
export const MemoryIcon = /* @__PURE__ */ appIcon(IconMemory)
export const MenuIcon = /* @__PURE__ */ appIcon(IconMenu2)
export const MoonIcon = /* @__PURE__ */ appIcon(IconMoon)
export const PackageIcon = /* @__PURE__ */ appIcon(IconPackage)
export const PaletteIcon = /* @__PURE__ */ appIcon(IconPalette)
export const PencilIcon = /* @__PURE__ */ appIcon(IconPencil)
export const PhotoIcon = /* @__PURE__ */ appIcon(IconPhoto)
export const PinIcon = /* @__PURE__ */ appIcon(IconPin)
export const PinnedOffIcon = /* @__PURE__ */ appIcon(IconPinnedOff)
export const PlayerPlayIcon = /* @__PURE__ */ appIcon(IconPlayerPlay)
export const PlayerStopIcon = /* @__PURE__ */ appIcon(IconPlayerStop)
export const PlugIcon = /* @__PURE__ */ appIcon(IconPlug)
export const PlusIcon = /* @__PURE__ */ appIcon(IconPlus)
export const RefreshIcon = /* @__PURE__ */ appIcon(IconRefresh)
export const RotateIcon = /* @__PURE__ */ appIcon(IconRotate)
export const SearchIcon = /* @__PURE__ */ appIcon(IconSearch)
export const SelectorIcon = /* @__PURE__ */ appIcon(IconSelector)
export const SendIcon = /* @__PURE__ */ appIcon(IconSend)
export const ServersIcon = /* @__PURE__ */ appIcon(IconServer)
export const SettingsIcon = /* @__PURE__ */ appIcon(IconSettings)
export const ShareIcon = /* @__PURE__ */ appIcon(IconShare)
export const ShieldCheckIcon = /* @__PURE__ */ appIcon(IconShieldCheck)
export const SignRightIcon = /* @__PURE__ */ appIcon(IconSignRight)
export const SparklesIcon = /* @__PURE__ */ appIcon(IconSparkles)
export const StrikethroughIcon = /* @__PURE__ */ appIcon(IconStrikethrough)
export const SunIcon = /* @__PURE__ */ appIcon(IconSun)
export const Terminal2Icon = /* @__PURE__ */ appIcon(IconTerminal2)
export const TrashIcon = /* @__PURE__ */ appIcon(IconTrash)
export const UnderlineIcon = /* @__PURE__ */ appIcon(IconUnderline)
export const UploadIcon = /* @__PURE__ */ appIcon(IconUpload)
export const UserCheckIcon = /* @__PURE__ */ appIcon(IconUserCheck)
export const UserCircleIcon = /* @__PURE__ */ appIcon(IconUserCircle)
export const UserMinusIcon = /* @__PURE__ */ appIcon(IconUserMinus)
export const UsersIcon = /* @__PURE__ */ appIcon(IconUsers)
export const WifiOffIcon = /* @__PURE__ */ appIcon(IconWifiOff)
export const XIcon = /* @__PURE__ */ appIcon(IconX)
