import { defineConfig } from "vitepress";

export default defineConfig({
  title: "Rivet",
  description: "End-to-end type safety between .NET and TypeScript",
  base: "/rivet/",
  head: [["link", { rel: "icon", href: "/rivet/logo.png" }]],

  themeConfig: {
    logo: "/logo.png",

    nav: [
      { text: "Guide", link: "/getting-started" },
      { text: "Reference", link: "/reference/cli" },
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
          { text: "Contracts", link: "/guides/contracts" },
          { text: "Contract Coverage", link: "/guides/contract-coverage" },
          { text: "Runtime Validation", link: "/guides/runtime-validation" },
          { text: "OpenAPI Emission", link: "/guides/openapi-emission" },
          { text: "OpenAPI Import", link: "/guides/openapi-import" },
          { text: "Error Handling", link: "/guides/error-handling" },
          { text: "File Uploads", link: "/guides/file-uploads" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "CLI", link: "/reference/cli" },
          { text: "Type Mapping", link: "/reference/type-mapping" },
          { text: "Attributes", link: "/reference/attributes" },
          { text: "Client Configuration", link: "/reference/client-config" },
          { text: "Route Definition", link: "/reference/endpoint-builder" },
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

    socialLinks: [
      { icon: "github", link: "https://github.com/maxanstey-meridian/rivet" },
    ],

    search: {
      provider: "local",
    },
  },
});
