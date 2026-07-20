import { useState } from "react"
import { useForm } from "react-hook-form"
import { zodResolver } from "@hookform/resolvers/zod"
import { CommandIcon } from "lucide-react"
import { z } from "zod"
import { api } from "@/lib/api"
import type { AuthStatusDto } from "@/lib/contracts"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Field, FieldDescription, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Spinner } from "@/components/ui/spinner"

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
    <main className="flex min-h-svh items-center justify-center bg-muted p-4">
      <Card className="w-full max-w-md">
        <CardHeader className="items-center text-center">
          <span className="flex size-10 items-center justify-center rounded-xl bg-primary text-primary-foreground"><CommandIcon /></span>
          <CardTitle><h1>{setup ? "Set up MC Panel" : "Welcome back"}</h1></CardTitle>
          <CardDescription>{setup ? "Create the single administrator account for this host." : "Sign in to manage your Minecraft servers."}</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit(submit)}>
            <FieldGroup>
              {problem && <Alert variant="destructive"><AlertTitle>Could not continue</AlertTitle><AlertDescription>{problem}</AlertDescription></Alert>}
              {setup && (
                <Field data-invalid={Boolean(errors.token)}>
                  <FieldLabel htmlFor="setup-token">One-time setup token</FieldLabel>
                  <Input id="setup-token" autoComplete="one-time-code" aria-invalid={Boolean(errors.token)} {...register("token")} />
                  <FieldDescription>Read this from the installer output or protected configuration file.</FieldDescription>
                  <FieldError errors={[errors.token]} />
                </Field>
              )}
              <Field data-invalid={Boolean(errors.username)}>
                <FieldLabel htmlFor="username">Username</FieldLabel>
                <Input id="username" autoComplete="username" aria-invalid={Boolean(errors.username)} {...register("username")} />
                <FieldError errors={[errors.username]} />
              </Field>
              <Field data-invalid={Boolean(errors.password)}>
                <FieldLabel htmlFor="password">Password</FieldLabel>
                <Input id="password" type="password" autoComplete={setup ? "new-password" : "current-password"} aria-invalid={Boolean(errors.password)} {...register("password")} />
                <FieldError errors={[errors.password]} />
              </Field>
              <Button type="submit" size="lg" disabled={isSubmitting}>{isSubmitting && <Spinner data-icon="inline-start" />}{setup ? "Create administrator" : "Sign in"}</Button>
            </FieldGroup>
          </form>
        </CardContent>
      </Card>
    </main>
  )
}
