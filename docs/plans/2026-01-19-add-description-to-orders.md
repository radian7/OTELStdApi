# Plan: Dodanie opcjonalnego pola Description do encji Orders

**Data:** 2026-01-19

## Opis

Rozszerzenie encji `Order` o opcjonalne pole `Description` (nullable string, max 500 znaków) z pełną obsługą w warstwie API, serwisów i bazy danych — zgodnie z architekturą Repository + UnitOfWork.

## Steps

1. **Dodaj właściwość do encji** — w `OTELStdApi/Data/Entities/Order.cs` dodaj `public string? Description { get; set; }` z atrybutem `[StringLength(500)]`

2. **Zaktualizuj konfigurację Fluent API** — w `OTELStdApi/Data/OrderDbContext.cs` dodaj `entity.Property(e => e.Description).HasMaxLength(500);` (bez `.IsRequired()`)

3. **Rozszerz interfejs serwisu** — w `OTELStdApi/Services/IOrderService.cs` dodaj opcjonalny parametr `string? description = null` do `CreateOrderAsync`

4. **Zaktualizuj implementację serwisu** — w `OTELStdApi/Services/OrderService.cs` dodaj parametr `string? description = null` i przypisz `Description = description` do encji `Order`

5. **Rozszerz DTO żądania** — w `OTELStdApi/Controllers/WeatherForecastController.cs` zaktualizuj record `CreateOrderRequest(string CustomerId, string CustomerType, decimal TotalAmount, string? Description = null)` i przekaż `request.Description` do serwisu

6. **Wygeneruj i zastosuj migrację** — wykonaj:
   ```bash
   dotnet ef migrations add AddDescriptionToOrders --context OrderDbContext --project OTELStdApi/OTELStdApi.csproj
   dotnet ef database update --context OrderDbContext --project OTELStdApi/OTELStdApi.csproj
   ```

## Pliki do modyfikacji

| Plik | Zmiana |
|------|--------|
| `OTELStdApi/Data/Entities/Order.cs` | Dodanie właściwości `Description` |
| `OTELStdApi/Data/OrderDbContext.cs` | Konfiguracja Fluent API |
| `OTELStdApi/Services/IOrderService.cs` | Rozszerzenie sygnatury metody |
| `OTELStdApi/Services/OrderService.cs` | Obsługa nowego parametru |
| `OTELStdApi/Controllers/WeatherForecastController.cs` | Rozszerzenie DTO i przekazanie do serwisu |
| `OTELStdApi/Migrations/` | Nowa migracja (generowana automatycznie) |
