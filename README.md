# OpenLibraryServer

Konzolna .NET Framework 4.8/4.8.1 aplikacija koja izlaže jednostavan Web server (preko HttpListener) za pretragu knjiga korišćenjem Open Library Search API-ja.
Projekat demonstrira klasično višenitno programiranje: dedikovana nit za accept petlju, ThreadPool za obradu zahteva, lock/ConcurrentDictionary/Timer/Interlocked za sinhronizaciju.
Keširanje odgovora u memoriji (TTL 5 min). Bez Task/await i bez dodatne JOIN deduplikacije.


## Zahtevi
- **Windows + Visual Studio 2022**
- Instaliran **.NET Framework 4.8/4.8.1 targeting pack**  
  VS Installer → *Modify* → **.NET desktop development** + **.NET Framework 4.8/4.8.1**
- NuGet: **Newtonsoft.Json** (v13+)
- Reference: **System.Web** (zbog `HttpUtility`)

## Brzi start
1. Kloniraj repo i otvori rešenje u **Visual Studio 2022**.
2. Proveri da je projekat **Console App (.NET Framework 4.8/4.8.1)**.
3. Instaliraj NuGet paket **Newtonsoft.Json**.
4. (Ako dobiješ 403 pri startu) pokreni **PowerShell kao Administrator** i izvrši:
   ```powershell
   netsh http add urlacl url=http://+:8080/ user=%USERNAME%
5. Pokreni aplikaciju (F5) i otvori u browseru: http://localhost:8080/

## Endpointi i primeri

Landing:
http://localhost:8080/

Health:
http://localhost:8080/health

Pretraga (JSON podrazumevano):

Autor:
/search?author=tolkien&limit=5

Slobodan tekst + limit:
/search?q=harry%20potter&limit=5

Naslov + stranica:
/search?title=the%20lord%20of%20the%20rings&page=2

HTML prikaz: dodaj &format=html
/search?author=tolkien&limit=5&format=html

Prosleđuju se samo dozvoljeni parametri (whitelist):
q, author, title, subject, isbn, publisher, sort, page, limit, fields, lang, offset.