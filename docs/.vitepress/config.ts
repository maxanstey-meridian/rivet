import { defineConfig } from "vitepress";

export default defineConfig({
  title: "Rivet",
  description: "C# in, OpenAPI 3.1 out — contract-first APIs for .NET",
  base: "/rivet/",
  head: [["link", { rel: "icon", href: "/rivet/logo.png" }]],

  themeConfig: {
    logo: "/logo.png",

    nav: [
      { text: "Get Started", link: "/getting-started" },
      { text: "Tutorial", link: "/guides/tutorial" },
      { text: "CLI", link: "/reference/cli" },
      {
        text: "NuGet",
        link: "https://www.nuget.org/packages/Rivet.Attributes",
      },
    ],

    sidebar: [
      {
        text: "Introduction",
        items: [
          { text: "What is Rivet?", link: "/" },
          { text: "Getting Started", link: "/getting-started" },
        ],
      },
      {
        text: "Guides",
        items: [
          { text: "Tutorial", link: "/guides/tutorial" },
          { text: "Contracts", link: "/guides/contracts" },
          { text: "Contract Coverage", link: "/guides/contract-coverage" },
          { text: "Error Handling", link: "/guides/error-handling" },
          { text: "File Uploads & Downloads", link: "/guides/file-uploads" },
          { text: "OpenAPI Emission", link: "/guides/openapi-emission" },
          { text: "OpenAPI Import", link: "/guides/openapi-import" },
          { text: "OpenAPI Round-Trips", link: "/guides/openapi-round-trips" },
          { text: "Runtime Validation", link: "/guides/runtime-validation" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "CLI", link: "/reference/cli" },
          { text: "Diagnostics", link: "/reference/diagnostics" },
          { text: "Attributes", link: "/reference/attributes" },
          { text: "Route Definition API", link: "/reference/endpoint-builder" },
          { text: "Type Mapping", link: "/reference/type-mapping" },
          { text: "Vendor Extensions", link: "/reference/vendor-extensions" },
          { text: "Import Profile", link: "/reference/import-profile" },
          { text: "Client Configuration", link: "/reference/client-config" },
        ],
      },
      {
        text: "Misc",
        items: [
          { text: "How It Works", link: "/misc/how-it-works" },
          { text: "Limitations", link: "/misc/limitations" },
        ],
      },
    ],

    socialLinks: [{ icon: "github", link: "https://github.com/maxanstey-meridian/rivet" }],

    search: {
      provider: "local",
    },
  },
});
