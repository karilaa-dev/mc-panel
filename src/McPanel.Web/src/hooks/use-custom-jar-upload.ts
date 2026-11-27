import { useMutation } from "@tanstack/react-query"
import { api } from "@/lib/api"
import { toast } from "@/lib/toast"

export function useCustomJarUpload() {
  return useMutation({
    mutationFn: api.uploadCustomJar,
    onSuccess: (jar) => toast.success("Custom JAR uploaded", { description: jar.fileName }),
    onError: (error) => toast.error(error.message || "Could not upload the JAR."),
  })
}
