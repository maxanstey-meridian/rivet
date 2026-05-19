import { defineConfig } from "vitepress";

export default defineConfig({
  title: "Rivet",
  description: "End-to-end type safety between .NET and TypeScript",
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
          { text: "Tutorial: Zero to Typed Client", link: "/guides/tutorial" },
        ],
      },
      {
        text: "Reference",
        items: [
          { text: "CLI", link: "/reference/cli" },
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
