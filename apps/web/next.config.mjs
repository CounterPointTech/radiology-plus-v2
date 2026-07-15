/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  typedRoutes: true,
  // Deployments copy .next/standalone (self-contained server, no node_modules
  // needed on the box) — see the prod bundle scripts.
  output: "standalone",
};

export default nextConfig;
