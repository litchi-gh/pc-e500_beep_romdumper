# ビルド手順

[English](BUILD_en.md)

> **実験的なアプリケーションです。** まだ十分に検証されていません。[README](../README.md#実験的なアプリケーションです) を先に読んでください。

BeepDump をソースからビルドする手順です。Windows で使うだけなら、ビルドは要りません（[INSTALL.md](INSTALL.md)）。Linux と macOS 用はビルドが必要です。

## 1. .NET 10 SDK を入れる

| OS | コマンドや方法 |
|---|---|
| Windows | `winget install Microsoft.DotNet.SDK.10` |
| Ubuntu 24.04、26.04 | `sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0` |
| Fedora | `sudo dnf install dotnet-sdk-10.0` |
| Debian 13 | 下を参照 |
| macOS | [ダウンロードページ](https://dotnet.microsoft.com/download/dotnet)で .NET 10 の SDK のインストーラーを入れる。Apple Silicon は **Arm64**、Intel は **x64** |

Debian 13 は、先に Microsoft のパッケージの配布元を追加します。

```sh
wget https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
```

ほかのディストリビューションは [Microsoft の説明](https://learn.microsoft.com/dotnet/core/install/linux)を見てください。

**新しいターミナルを開き直して**確かめます。10 から始まる番号が出れば大丈夫です。

```sh
dotnet --version
```

## 2. ソースを取得する

```sh
git clone https://github.com/litchi-gh/pc-e500_beep_romdumper.git
cd pc-e500_beep_romdumper/BeepDump
```

Git がなければ、GitHub のページの Code → Download ZIP で取得して展開しても同じです。

## 3. ビルドする

インターネット接続が必要です。

### Windows

```powershell
.\build.cmd            # テストのあと、Windows 用の GUI とコマンドライン版を作る
.\build.cmd -Cli       # さらに Linux と macOS 用のコマンドライン版も作る
.\build.cmd -TestOnly  # テストだけ
```

テストに数分かかります。`BeepDump.exe` を開いたままだと更新できないので、閉じてから実行します。

### Linux、macOS

`build.cmd` は Windows 用なので、`dotnet publish` を直接実行します。画面のあるアプリは Windows でしかビルドできません。

```sh
dotnet publish src/BeepDump.Cli -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None \
  -o app/cli/linux-x64
```

`linux-x64` の 2 か所を、機械に合わせて換えます。

| 機械 | 指定 |
|---|---|
| Linux（x64） | `linux-x64` |
| Linux（Arm64） | `linux-arm64` |
| macOS（Apple Silicon） | `osx-arm64` |
| macOS（Intel） | `osx-x64` |

> **未確認:** Linux と macOS の上でのビルドは試していません。確認したのは、Windows で `.\build.cmd -Cli` を使ってビルドできることと、できた `linux-x64` 版を WSL の Ubuntu で動かすことです。

ビルドせずにそのまま動かすこともできます。

```sh
dotnet run --project src/BeepDump.Cli -c Release -- decode 録音.wav
```

### できるファイル

`BeepDump/app/` の下にできます。どれも単体で動き、.NET のインストールは要りません。

| ファイル | 内容 | 大きさ |
|---|---|---|
| `BeepDump.exe` | 画面のあるアプリ（Windows） | 約 50MB |
| `cli/win-x64/beepdump.exe` | コマンドライン版（Windows） | 約 36MB |
| `cli/linux-x64/beepdump` | コマンドライン版（Linux x64） | 約 38MB |
| `cli/linux-arm64/beepdump` | コマンドライン版（Linux Arm64、未確認） | 約 36MB |
| `cli/osx-x64/beepdump` | コマンドライン版（macOS Intel、未確認） | 約 37MB |
| `cli/osx-arm64/beepdump` | コマンドライン版（macOS Apple Silicon、未確認） | 約 35MB |

置き場所は [INSTALL.md](INSTALL.md) を見てください。

## テストだけ動かす

```sh
dotnet run --project tests/BeepDump.Tests -c Release
dotnet run --project tests/BeepDump.Tests -c Release -- "fast:"   # 名前の一部を指定して絞る
```

全部合格すると `All tests passed.` と出ます。

## Windows のリリースを作る

[INSTALL.md](INSTALL.md) は、Releases の Assets に次の 2 つがある前提で書いています。

| Assets の名前 | 中身 |
|---|---|
| `BeepDump.exe` | `app\BeepDump.exe` |
| `beepdump-win-x64.zip` | `app\cli\win-x64\beepdump.exe` を zip にしたもの |

`BeepDump.exe` と `beepdump.exe` は、Windows では同じ名前として扱われるので、コマンドライン版は zip に入れます。

```powershell
.\build.cmd
Compress-Archive -Path app\cli\win-x64\beepdump.exe -DestinationPath beepdump-win-x64.zip -Force
```

## 機械語、リスト、図を作り直す

アプリのビルドには要りません。変更するときだけ使います。コマンドはリポジトリの一番上のフォルダで実行します。

`fast\build.py` には、SC62015 のアセンブラ `e500asm` と、テキストを CE-140F の BAS に変換する `ce140conv` が必要です。`PATH` に置くか、環境変数 `E500ASM` と `CE140CONV` で場所を指定します。生成したファイルはリポジトリに入っているので、機械語やリストを変えないなら要りません。

| 作るもの | コマンド | 必要なもの |
|---|---|---|
| 機械語、リスト、リストの画像、README のリスト | `py fast\build.py` | Python、Pillow（`py -m pip install pillow`）、`e500asm.exe`、`ce140conv.exe`、Windows のフォント（Consolas、メイリオ） |
| 図 | `py docs\figures\build_figures.py` | Python |
| 図のデータ | `py docs\figures\extract_data.py` | Python、NumPy、SciPy、実機の録音（リポジトリにはありません） |

## 困ったとき

| 症状 | 確かめること |
|---|---|
| `dotnet` が見つからない | SDK を入れたあと、ターミナルを開き直してください |
| `dotnet --version` が 10 で始まらない | .NET 10 SDK を入れてください。ほかの版と共存できます |
| `Access to the path ... is denied` や `publish failed` | `BeepDump.exe` が開いたままです。閉じてからビルドしてください |
| PowerShell でスクリプトの実行が禁止される | `build.ps1` ではなく `build.cmd` を実行してください |
| パッケージのダウンロードに失敗する | インターネット接続を確かめてください。ほかの OS 用のビルドでは、実行環境をダウンロードします |
