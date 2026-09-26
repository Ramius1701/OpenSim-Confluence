# Setting up Confluence

Confluence runs on **SQLite**, **MySQL / MariaDB** and **PostgreSQL**, in **standalone** mode (one
`OpenSim.exe`) or **grid** mode (a `Robust.exe` plus one or more regions). This guide takes you from a
clean checkout to a running world for any of those six combinations.

Every step here is exercised from a fresh clone by `Tools/fresh-clone-matrix.py` (see
[How this guide is tested](#how-this-guide-is-tested)), using only the `*.ini.example` files that ship
with the repository. If something here does not work on a clean machine, that is a bug in Confluence or
in this guide - please report it.

## 1. Requirements

- The [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) and `git`.
- For MySQL / MariaDB or PostgreSQL: a running database server and an account allowed to create a
  database. SQLite needs nothing extra.
- Grid mode: a machine name or address your regions and viewers can reach (`127.0.0.1` for a test).

## 2. Build

```bat
git clone https://github.com/Ramius1701/OpenSim-Confluence.git
cd OpenSim-Confluence
runprebuild.bat
dotnet build OpenSim.sln --configuration Release
```

(`./runprebuild.sh` on Linux and macOS.) The finished build is the `bin` folder. Copy it to a deployment
folder (for example `C:\confluence`) and work there; never edit files in the build output you intend to
rebuild over.

Configuration is never installed for you. Each `*.ini.example` file below is copied to the same name
without `.example` and edited.

## 3. Choose a database

| | Standalone | Grid |
|---|---|---|
| **SQLite** | works | works |
| **MySQL / MariaDB** | works | works |
| **PostgreSQL** | works | works |

Every feature works on MySQL / MariaDB. A few optional features do not have a SQLite or PostgreSQL
backend yet; they fail to load with a clear error and everything else keeps working. The current list is
in [Known gaps](#known-gaps-by-database).

Database connection settings, in the one place you set them:

**SQLite** - nothing to do. The shipped `Include-Storage = "config-include/storage/SQLite...ini"` line
gives each service its own database file next to the program.

**MySQL / MariaDB** - create an empty database and a user for it, then use these two lines:

```sql
CREATE DATABASE confluence CHARACTER SET utf8mb4;
CREATE USER 'confluence'@'localhost' IDENTIFIED BY 'choose-a-password';
GRANT ALL PRIVILEGES ON confluence.* TO 'confluence'@'localhost';
```

```ini
StorageProvider = "OpenSim.Data.MySQL.dll"
ConnectionString = "Data Source=localhost;Port=3306;Database=confluence;User ID=confluence;Password=choose-a-password;Old Guids=true;SslMode=None;"
```

**PostgreSQL** - create an empty database and a user for it, then use these two lines:

```sql
CREATE USER confluence WITH PASSWORD 'choose-a-password';
CREATE DATABASE confluence OWNER confluence;
```

```ini
StorageProvider = "OpenSim.Data.PGSQL.dll"
ConnectionString = "Server=localhost;Port=5432;Database=confluence;User Id=confluence;Password=choose-a-password;"
```

With MySQL or PostgreSQL, delete (or comment out) the template's `Include-Storage` line in the same
section. All of Confluence's own services (accounts, sessions, settings, store, currency, and so on)
inherit this one connection; you do not edit each service's section.

## 4. Standalone mode

1. In your deployment folder, copy `OpenSim.ini.example` to `OpenSim.ini`. Leave
   `Include-Architecture = "config-include/Standalone.ini"` as it is.
2. Copy `config-include/StandaloneCommon.ini.example` to `config-include/StandaloneCommon.ini`, and
   `config-include/osslEnable.ini.example` and `config-include/FlotsamCache.ini.example` to the same names
   without `.example` (the templates include them; without them you get a warning at start).
3. Set your database in `StandaloneCommon.ini`'s `[DatabaseService]` (section 3). SQLite needs no change.
4. Regions: on a first run OpenSim asks a few console questions about your first region and estate. To
   start unattended instead, create `Regions/Regions.ini` and add a default estate to `OpenSim.ini`:

   ```ini
   [Estates]
       DefaultEstateName = My Estate
       DefaultEstateOwnerName = First Last
       DefaultEstateOwnerUUID = 00000000-0000-4000-8000-000000000001
       DefaultEstateOwnerEMail = you@example.org
       DefaultEstateOwnerPassword = choose-a-password
   ```

5. Run `OpenSim.exe`. The web interface is on the same process's HTTP port (`http_listener_port`, 9000 by
   default). Turn on optional modules such as the region greeter with `[Concierge] enabled = true`.

## 5. Grid mode

Grid mode is Robust plus regions. Start Robust first; regions register with it.

**Robust**

1. Copy `Robust.HG.ini.example` to `Robust.HG.ini` (or `Robust.ini.example` for a non-Hypergrid grid).
2. In `[Const]` set `BaseHostname` to the name or address regions and viewers use, and the two ports
   (`PublicPort` and `PrivatePort`; keep the private one closed at your firewall).
3. In `[DatabaseService]` set your database (section 3). For SQLite change the active line to
   `Include-Storage = "config-include/storage/SQLiteRobust.ini";`. Every service section below it inherits
   this connection.
4. Optional: turn on the in-world profile service by uncommenting `UserProfilesServiceConnector` in
   `[ServiceList]` and setting `Enabled = true` in `[UserProfilesService]`.
5. Run `Robust.exe -inifile=Robust.HG.ini`. Add `-background=true` to run with no console window (for a
   service or Docker). On first start Robust logs a temporary password for the admin account, see
   [First login](#6-first-login).

**A region** (in the same or another deployment folder)

1. Copy `OpenSim.ini.example` to `OpenSim.ini`; set `Include-Architecture` to
   `config-include/GridHypergrid.ini` (or `Grid.ini`); in `[Const]` set the same `BaseHostname`,
   `PublicPort` and `PrivatePort` as Robust; set `[Network] http_listener_port` to a port no other
   region uses.
2. Copy `config-include/GridCommon.ini.example` to `GridCommon.ini` and set the region's own database in
   its `[DatabaseService]` (section 3). Copy the two `.example` includes as in section 4.
3. Add `Regions/Regions.ini` with one `[Region Name]` block (a `RegionUUID`, a `Location`, `SizeX/Y/Z`, an
   `InternalPort`, `ExternalHostName`).
4. The estate needs an owner that exists on the grid. Use the first-login admin account (section 6) as
   `DefaultEstateOwnerName` in an `[Estates]` block like the standalone one (without the UUID, email and
   password lines), or answer the console questions on first run.
5. Run `OpenSim.exe` (add `-background=true -console=rest` to run headless).

You can also provision regions from the web interface once you are logged in: **Create Region** in the
admin area copies a template `OpenSim.ini` into its own folder and starts it. See the Deployment section
of the README for that flow and for rolling restarts.

## 6. First login

On a genuinely fresh database the web interface creates one admin account, **Grid Admin**, with a random
temporary password written **once** to the log (`Robust.log` in grid mode, `OpenSim.log` in standalone
mode, look for "Temporary password"). Sign in at `http://<BaseHostname>:<PublicPort>/` and you are made to
choose a real password before anything else. There is nothing to create by hand.

## Known gaps by database

These optional features fail to load, with a clear error, on the databases shown; everything else works.
The machine-readable list is `Tools/fresh-clone-matrix-expected.json`, and the goal is to empty it.

| Feature | SQLite | PostgreSQL | MySQL / MariaDB |
|---|---|---|---|
| Native Marketplace | not yet | not yet | works |
| Region Hypergrid records (RegionHGService) | not yet | not yet | works |
| Offline instant messages | not yet | works | works |
| Groups (data and search) | not yet | works | works |
| Disk asset store (FSAssets, optional) | not yet | works | works |

## How this guide is tested

```bat
python Tools\fresh-clone-matrix.py --dbs sqlite,mysql,pgsql --modes standalone,grid
```

The script clones the repository, builds it with the steps in section 2, then for each database and mode
generates a deployment from the shipped templates, starts it, and checks that Robust and the web interface
answer, the first admin account is created, the region reaches "Startup complete", and no service fails to
load beyond the known gaps. Add `--include-uncommitted` to test your working tree before committing.
MySQL and PostgreSQL logins are read from `CFX_MYSQL_ADMIN` and `CFX_PGSQL_ADMIN`
(`user:password@host:port`); a database without one is skipped.
