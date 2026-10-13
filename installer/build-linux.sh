#!/usr/bin/env bash
# 构建 IGW Linux 发布包:installer/out/igw-<版本>-linux-x64.tar.gz
# 用法(Git Bash / WSL 皆可):./build-linux.sh [版本号,默认读 version.txt]
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
VER="${1:-$(tr -d '\r\n' < "$HERE/version.txt")}"
STAGE="$HERE/out/linux-stage"
OUT="$HERE/out/igw-$VER-linux-x64.tar.gz"

echo "[0/3] 构建内嵌 Web 界面(Vue)-> IGW.Runtime/wwwroot ..."
( cd "$ROOT/src/IGW.WebUi" && pnpm install --silent && pnpm build )

echo "[1/3] dotnet publish Runtime linux-x64(自包含,含 wwwroot)..."
rm -rf "$STAGE"
mkdir -p "$STAGE/igw/app"
dotnet publish "$ROOT/src/IGW.Runtime/IGW.Runtime.csproj" -c Release -r linux-x64 --self-contained true \
  -o "$STAGE/igw/app" -v q --nologo
bash "$HERE/package-questdb.sh" "$STAGE/igw/app"

echo "[2/3] 放入安装脚本/服务/命令..."
cp "$ROOT/tools/linux/install.sh" "$STAGE/igw/"
cp "$ROOT/tools/linux/igwctl"     "$STAGE/igw/"
cp "$ROOT/tools/linux/igw.service" "$STAGE/igw/"
chmod +x "$STAGE/igw/install.sh" "$STAGE/igw/igwctl" "$STAGE/igw/app/IGW.Runtime" 2>/dev/null || true
# 保险:确保脚本是 LF(Windows 上编辑过也能装)
sed -i 's/\r$//' "$STAGE/igw/install.sh" "$STAGE/igw/igwctl" "$STAGE/igw/igw.service" 2>/dev/null || true

echo "[3/3] 打包 $OUT ..."
rm -f "$OUT"
tar -C "$STAGE" -czf "$OUT" igw
echo "完成:$OUT"
echo "部署:拷到 Linux → tar xzf 解包 → cd igw → sudo ./install.sh"
