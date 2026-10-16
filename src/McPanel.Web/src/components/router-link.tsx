import { forwardRef } from "react"
import { Link } from "react-router-dom"
import type { LinkComponentProps } from "@cloudflare/kumo"

const documentLink = /^(?:[a-z][a-z\d+.-]*:|\/\/|\/api\/|\/hubs\/)/i

/** Bridges Kumo links to React Router while keeping downloads and external URLs as plain anchors. */
export const RouterLink = forwardRef<HTMLAnchorElement, LinkComponentProps>(function RouterLink({ href, to, ...props }, ref) {
  const destination = href ?? to ?? ""
  if (documentLink.test(destination) || props.download !== undefined || props.target === "_blank")
    return <a ref={ref} href={destination} {...props} />
  return <Link ref={ref} to={destination} {...props} />
})
