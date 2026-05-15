# BookingApi — 工作坊練習用 Codebase

> **Tech Lead 的 Agentic Coding 導入與治理工作坊** 實機體驗練習用程式碼

## 這是什麼

這是一個預約管理系統的 API，模擬一間多租戶預約平台的後端服務。功能包含排班查詢、建立預約、取消預約、預約列表查詢、每日營收統計。

這份程式碼是工作坊 M3（實機體驗）環節的練習素材。學員會使用 Agentic Coding 工具（如 Claude Code）對這份 codebase 執行 `/init`，產生 CLAUDE.md，然後以 Tech Lead 的角色審查 AI 生成的內容。

## 技術棧

- .NET 8 / C#
- ASP.NET Core Minimal API
- Entity Framework Core 8
- PostgreSQL（Npgsql）

## 專案結構

```
BookingApi/
├── Program.cs          # 所有 API endpoints + Models + DbContext（單檔）
├── BookingApi.csproj    # 專案設定
├── appsettings.json     # 設定檔
└── Properties/
    └── launchSettings.json
```

## API Endpoints

| Method | Path | 說明 |
|--------|------|------|
| GET | `/api/staff/on-duty` | 查詢目前在班的服務人員 |
| POST | `/api/bookings` | 建立預約（含價格計算、時段衝突檢查） |
| POST | `/api/bookings/{id}/cancel` | 取消預約 |
| GET | `/api/bookings` | 查詢預約列表（支援篩選、分頁） |
| GET | `/api/reports/daily-revenue` | 每日營收統計 |

## 業務規則

- 多租戶架構：所有操作以 `ShopId`（從 JWT claim 取得）隔離
- 預約時長必須是 30 分鐘的倍數
- 時段衝突檢查：同一位服務人員不可重複預約
- 價格計算：基本價 × 時段數，VIP 九折，尖峰時段（週末 + 平日 18-22 時）加兩成
- 客戶累計 10 次到訪自動升級 VIP
- 取消限制：預約前 2 小時內不可取消

## 練習目的

這份 codebase 刻意以**單檔、無測試、混合關注點**的方式撰寫，反映真實世界中常見的遺留系統特徵。學員的任務不是修改程式碼，而是以 Tech Lead 的角色：

1. 讓 AI 工具對這份 codebase 執行 `/init`，觀察 AI 生成的 CLAUDE.md
2. 以三層架構的視角審查：AI 看到了什麼、漏掉了什麼
3. 補強 CLAUDE.md 中缺少的護欄和業務規則

## 注意事項

- 這是練習用的程式碼，不需要實際執行
- 不需要安裝 PostgreSQL 或設定資料庫連線
- 學員在工作坊現場使用自己的筆電和 AI 工具操作

## License

本專案僅供 ClearForge 工作坊教學使用。
