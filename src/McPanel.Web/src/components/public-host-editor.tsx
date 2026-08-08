import { useState, type FormEvent } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { CheckIcon } from "lucide-react"
import { toast } from "sonner"
import { api } from "@/lib/api"
import { Button } from "@/components/ui/button"
import { Field, FieldDescription, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { InputGroup, InputGroupAddon, InputGroupButton, InputGroupInput } from "@/components/ui/input-group"
import { Spinner } from "@/components/ui/spinner"

export function PublicHostEditor({ serverId, value, addressRevision, inheritedPreview, compact = false }: {
  serverId: string
  value?: string | null
  addressRevision: string
  inheritedPreview?: string | null
  compact?: boolean
}) {
  return <PublicHostEditorForm key={`${addressRevision}-${value ?? ""}`} serverId={serverId} value={value} addressRevision={addressRevision} inheritedPreview={inheritedPreview} compact={compact} />
}

function PublicHostEditorForm({ serverId, value, addressRevision, inheritedPreview, compact }: {
  serverId: string
  value?: string | null
  addressRevision: string
  inheritedPreview?: string | null
  compact: boolean
}) {
  const [address, setAddress] = useState(value ?? "")
  const queryClient = useQueryClient()
  const save = useMutation({
    mutationFn: () => api.setServerPublicAddress(serverId, address.trim() || null, addressRevision),
    onSuccess: (server) => {
      queryClient.setQueryData(["server", serverId], server)
      void queryClient.invalidateQueries({ queryKey: ["servers"] })
      void queryClient.invalidateQueries({ queryKey: ["gate"] })
      toast.success("Advertised connection address saved")
    },
    onError: (error) => toast.error(error.message),
  })
  function submit(event: FormEvent) {
    event.preventDefault()
    if (address.trim() !== (value ?? "")) save.mutate()
  }
  if (compact) return <form className="max-w-xl" onSubmit={submit}><InputGroup><InputGroupInput aria-label="Advertised connection address" placeholder="play.example.com:25570" value={address} onChange={(event) => setAddress(event.target.value)} /><InputGroupAddon align="inline-end"><InputGroupButton type="submit" disabled={save.isPending || address.trim() === (value ?? "")}>{save.isPending ? <Spinner /> : <CheckIcon />}<span className="sr-only">Save advertised connection address</span></InputGroupButton></InputGroupAddon></InputGroup></form>
  return <form onSubmit={submit}><Field><FieldLabel htmlFor={`advertised-address-${serverId}`}>Advertised connection address</FieldLabel><div className="flex flex-col gap-2 sm:flex-row"><Input id={`advertised-address-${serverId}`} placeholder="play.example.com:25570" value={address} onChange={(event) => setAddress(event.target.value)} /><Button type="submit" variant="outline" disabled={save.isPending || address.trim() === (value ?? "")}>{save.isPending && <Spinner data-icon="inline-start" />}Save address</Button></div><FieldDescription>Use a hostname, IPv4 address, or bracketed IPv6 address. A custom host without a port means 25565. Leave blank to inherit {inheritedPreview ?? "the global server address and this server’s real port"}; advertised ports may be external NAT, SRV, or proxy mappings.</FieldDescription></Field></form>
}
