import { useState, type FormEvent } from "react"
import { useMutation, useQueryClient } from "@tanstack/react-query"
import { Button, Input, InputGroup } from "@cloudflare/kumo"
import { CheckIcon } from "@phosphor-icons/react"
import { api } from "@/lib/api"
import { toast } from "@/lib/toast"

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
  const changed = address.trim() !== (value ?? "")
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
    if (changed) save.mutate()
  }
  const description = `Use a hostname, IPv4 address, or bracketed IPv6 address. A host without a port means 25565. Leave blank to inherit ${inheritedPreview ?? "the global server address and this server’s real port"}.`
  if (compact) return <form onSubmit={submit} className="flex flex-col gap-2">
    <InputGroup label="Advertised connection address">
      <InputGroup.Input placeholder={inheritedPreview ?? "play.example.com:25570"} value={address} onChange={(event) => setAddress(event.target.value)} />
      <InputGroup.Addon align="end">
        <InputGroup.Button type="submit" shape="square" icon={CheckIcon} aria-label="Save advertised connection address" loading={save.isPending} disabled={!changed} />
      </InputGroup.Addon>
    </InputGroup>
    <p className="text-sm text-kumo-subtle">{description}</p>
  </form>
  return <form onSubmit={submit} className="flex flex-col gap-3 sm:flex-row sm:items-start">
    <div className="flex-1"><Input label="Advertised connection address" placeholder="play.example.com:25570" value={address} onChange={(event) => setAddress(event.target.value)} description={`${description} Advertised ports may be external NAT, SRV, or proxy mappings.`} /></div>
    <Button type="submit" className="sm:mt-7" loading={save.isPending} disabled={!changed}>Save address</Button>
  </form>
}
