import { defineConfig } from 'vitepress'
import { sidebar } from './navigation.mjs'

const base = process.env.DOCS_BASE || '/'

export default defineConfig({
  lang: 'zh-CN',
  title: '消息岛',
  description: '消息岛使用指南、下载与插件开发文档。',
  base,
  srcExclude: ['node_modules/**'],
  head: [
    ['meta', { name: 'theme-color', content: '#168b8f' }],
    ['link', { rel: 'icon', type: 'image/x-icon', href: `${base}favicon.ico` }]
  ],
  markdown: {
    lineNumbers: true
  },
  themeConfig: {
    siteTitle: '消息岛文档',
    logo: { src: '/logo.png', alt: '消息岛' },
    nav: [
      { text: '首页', link: '/', activeMatch: '^/$' },
      { text: '使用', link: '/guide/getting-started', activeMatch: '^/guide/' },
      { text: '下载', link: '/download/', activeMatch: '^/download/' },
      { text: '插件开发', link: '/plugins', activeMatch: '^/plugins(?:[/.]|$)' }
    ],
    sidebar,
    socialLinks: [{ icon: 'github', link: 'https://github.com/Cloudwhile/WindowsIsland' }],
    outline: { label: '本页目录', level: [2, 3] },
    docFooter: { prev: '上一篇', next: '下一篇' },
    sidebarMenuLabel: '目录',
    returnToTopLabel: '回到顶部',
    darkModeSwitchLabel: '切换主题',
    lightModeSwitchTitle: '切换到浅色主题',
    darkModeSwitchTitle: '切换到深色主题',
    skipToContentLabel: '跳转到正文',
    externalLinkIcon: true,
    editLink: {
      pattern: 'https://github.com/Cloudwhile/WindowsIsland/edit/master/docs/:path',
      text: '在 GitHub 上编辑此页'
    },
    search: {
      provider: 'local',
      options: {
        miniSearch: {
          options: {
            tokenize: (text) => {
              const segmenter = new Intl.Segmenter('zh-CN', { granularity: 'word' })
              return [...segmenter.segment(text)]
                .filter((part) => part.isWordLike)
                .map((part) => part.segment)
            }
          }
        },
        translations: {
          button: { buttonText: '搜索文档', buttonAriaLabel: '搜索文档' },
          modal: {
            displayDetails: '显示详细结果',
            resetButtonTitle: '清空搜索',
            backButtonTitle: '关闭搜索',
            noResultsText: '未找到相关内容',
            footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' }
          }
        }
      }
    }
  }
})
