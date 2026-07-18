import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  poweredByHeader: false,
  images: {
    unoptimized: true,
  },
  outputFileTracingExcludes: {
    "/*": ["node_modules/sharp/**/*", "node_modules/@img/**/*"],
  },
};

export default nextConfig;
