import { useState } from "react"
import { useForm } from "react-hook-form"
import { zodResolver } from "@hookform/resolvers/zod"
import { z } from "zod"
import { Banner, Button, Input, LayerCard } from "@cloudflare/kumo"
import { PasswordInput } from "@/components/controls"
import { AlertCircleFilledIcon } from "@/components/icons"
import { api } from "@/lib/api"
import type { AuthStatusDto } from "@/lib/contracts"

const schema = z.object({
  token: z.string().optional(),
  username: z.string().min(3, "Use at least 3 characters."),
  password: z.string().min(12, "Use at least 12 characters."),
})
type Values = z.infer<typeof schema>

export function AuthScreen({ status }: { status: AuthStatusDto }) {
  const [problem, setProblem] = useState<string>()
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { token: "", username: "admin", password: "" },
  })
  const setup = status.setupRequired

  async function submit(values: Values) {
    setProblem(undefined)
    try {
      if (setup) await api.setup({ token: values.token ?? "", username: values.username, password: values.password })
      else await api.login({ username: values.username, password: values.password })
      window.location.reload()
    } catch (error) {
      setProblem(error instanceof Error ? error.message : "Sign in failed.")
    }
  }

  return (
    <main className="flex min-h-svh items-center justify-center bg-kumo-canvas p-4 sm:p-8">
      <div className="flex w-full max-w-sm flex-col gap-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <img src="/mcpanel.svg" alt="" className="size-12" />
          <div className="flex flex-col gap-1">
            <h1 className="text-2xl font-semibold tracking-tight text-kumo-strong">{setup ? "Set up MC Panel" : "Welcome back"}</h1>
            <p className="text-base text-kumo-subtle">{setup ? "Create the single administrator account for this host." : "Sign in to manage your Minecraft servers."}</p>
          </div>
        </div>
        <LayerCard className="p-6">
          <form className="flex flex-col gap-5" onSubmit={handleSubmit(submit)} noValidate>
            {problem && <Banner variant="error" size="sm" icon={<AlertCircleFilledIcon />} title="Could not continue" description={problem} />}
            {setup && <Input
              label="One-time setup token"
              autoComplete="one-time-code"
              passwordManagerIgnore
              description="Find it in the installer output or the latest service startup log. Restarting the panel generates a new token."
              error={errors.token?.message}
              {...register("token")}
            />}
            <Input label="Username" autoComplete="username" error={errors.username?.message} {...register("username")} />
            <PasswordInput
              label="Password"
              autoComplete={setup ? "new-password" : "current-password"}
              error={errors.password?.message}
              {...register("password")}
            />
            <Button type="submit" variant="primary" size="lg" loading={isSubmitting} className="w-full justify-center">{setup ? "Create administrator" : "Sign in"}</Button>
          </form>
        </LayerCard>
      </div>
    </main>
  )
}
