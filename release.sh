#!/usr/bin/env bash
# ============================================================================
# FlexBot 发布打包
#
#   ./release.sh [版本号] [输出根目录]
#
# 产物（默认落在 publish/release/<版本>/）：
#   FlexBot-<版本>-win-x64.zip          win-x64        ┐
#   FlexBot-<版本>-linux-x64.zip        linux-x64      │ 只有宿主 + .NET 运行时
#   FlexBot-<版本>-linux-arm64.zip      linux-arm64    │ （自包含，无需另装 .NET）
#   FlexBot-<版本>-osx-x64.zip          osx-x64        │ 不带 plugins/，宿主首次运行自动创建
#   FlexBot-<版本>-osx-arm64.zip        osx-arm64      ┘
#   FlexBot-BuiltInPlugins-<版本>.zip   8 个内置插件（平台无关；条目带 plugins/ 前缀，
#                                       在 WebUI「添加插件」里选它可一次装多个，或解压覆盖）
#
# 注意：宿主与插件是两个发布图——宿主 publish 不会带上 plugins/，插件必须各自 publish；
# 内置插件包不带 -r（纯托管程序集），因此一个包可给所有平台用。
# ============================================================================
set -euo pipefail

VER="${1:-1.0.0}"
OUT_ROOT="${2:-publish/release/$VER}"

# MSYS2/MinGW 下 PATH 可能被裁剪，补上标准工具位置
export PATH="/usr/bin:/bin:/mingw64/bin:$PATH"

REPO="$(cd "$(dirname "$0")" && pwd)"
DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null 2>&1 || { echo "错误：找不到 dotnet" >&2; exit 1; }

PLUGINS=(Admin Agent AOnline FileSystem PCControl PluginApi PluginBuilder RedPacketWatch)
TARGETS=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)
# Roslyn 的本地化诊断资源：只保留 zh-Hans，其余删掉（报错回落英文）
DROP_LANGS=(cs de es fr it ja ko pl pt-BR ru tr zh-Hant)

# 生成 zip：优先 zip（Linux/macOS/MSYS2），没有就退回 bsdtar（Windows 自带）
zip_dir() { # $1=目录 $2=输出 zip（可为相对路径：内部会先解析成绝对，避免 cd 后失效）
  local dir="$1" out="$2" abs out_abs
  abs="$(cd "$dir" && pwd)"
  out_abs="$(cd "$(dirname "$out")" && pwd)/$(basename "$out")"
  rm -f "$out_abs"
  if command -v zip >/dev/null 2>&1; then
    # -@ 从 stdin 读文件列表；去掉 ./ 前缀，保证条目是干净的相对路径且用 / 分隔
    ( cd "$abs" && find . -mindepth 1 -print | sed 's|^\./||' | zip -qr9 "$out_abs" -@ )
  else
    ( cd "$abs" && tar -a -c -f "$out_abs" . )
  fi
}

publish_host() { # $1=rid $2=输出目录（自包含：带 .NET 运行时）
  "$DOTNET" publish "FlexBot/FlexBot.csproj" -c Release -r "$1" -o "$2" --self-contained \
    -p:DebugType=none -p:DebugSymbols=false --nologo -v q
}

publish_plugin() { # $1=项目 $2=输出目录（不带 -r：纯托管，平台无关）
  "$DOTNET" publish "$1" -c Release -o "$2" --no-self-contained \
    -p:DebugType=none -p:DebugSymbols=false --nologo -v q
}

mkdir -p "$OUT_ROOT"
cd "$REPO"

report=""
for rid in "${TARGETS[@]}"; do
  out="$OUT_ROOT/$rid"
  echo "=== $rid ==="
  rm -rf "$out"; mkdir -p "$out"

  publish_host "$rid" "$out"

  # 发布包不带运行期状态与个人配置；plugins/ 故意不放（宿主启动时自动创建，插件由插件包装）
  rm -f  "$out/config.json"
  rm -rf "$out/logs" "$out/memory"
  find "$out" -type f -name '*.pdb' -delete
  for l in "${DROP_LANGS[@]}"; do rm -rf "${out:?}/$l"; done

  # 包内附使用者说明；仓库若有 LICENSE 也一起带上（许可协议明确时自动生效）
  cp "$REPO/使用说明.txt" "$out/使用说明.txt"
  [ -f "$REPO/LICENSE" ] && cp "$REPO/LICENSE" "$out/LICENSE"
  [ -f "$REPO/THIRD-PARTY-NOTICES.txt" ] && cp "$REPO/THIRD-PARTY-NOTICES.txt" "$out/THIRD-PARTY-NOTICES.txt"
  zip_dir "$out" "$OUT_ROOT/FlexBot-${VER}-${rid}.zip"
  echo "  -> FlexBot-${VER}-${rid}.zip"
  report="${report}$(basename "$out") $(du -sm "$out" | cut -f1)MB->$(du -m "$OUT_ROOT/FlexBot-${VER}-${rid}.zip" | cut -f1)MB\n"
done

# ---- 内置插件包（平台无关）----
echo "=== FlexBot-BuiltInPlugins-${VER}.zip ==="
plug_root="$OUT_ROOT/.plug"
rm -rf "$plug_root"; mkdir -p "$plug_root/plugins"
for p in "${PLUGINS[@]}"; do
  publish_plugin "FlexBot.BuiltInPlugins/$p/$p.csproj" "$plug_root/plugins/$p"
done
# 插件目录里不该出现宿主程序集与运行期产物（各插件 csproj 的 StripHostAssemblies 已删大部分，这里兜底）
find "$plug_root" -type f \( -name 'FlexBot.dll' -o -name 'FlexBot.exe' -o -name 'FlexBot.Abstractions.dll' \
  -o -name 'OneBotLib.dll' -o -name 'Photino.NET.dll' -o -name '*.pdb' \) -delete
find "$plug_root" -type d \( -name 'libs' -o -name '.cache' -o -name 'backups' \) -exec rm -rf {} + 2>/dev/null || true
cp "$REPO/THIRD-PARTY-NOTICES.txt" "$plug_root/THIRD-PARTY-NOTICES.txt"
cp "$REPO/LICENSE" "$plug_root/LICENSE"
rm -f "$plug_root"/plugins/*/config.json "$plug_root"/plugins/*/settings.json
plug_unzip_mb=$(du -sm "$plug_root" | cut -f1)
zip_dir "$plug_root" "$OUT_ROOT/FlexBot-BuiltInPlugins-${VER}.zip"
plug_zip_mb=$(du -m "$OUT_ROOT/FlexBot-BuiltInPlugins-${VER}.zip" | cut -f1)
rm -rf "$plug_root"
echo "  -> FlexBot-BuiltInPlugins-${VER}.zip"

# ---- 校验文件（供用户核对下载完整性）----
if command -v sha256sum >/dev/null 2>&1; then
  ( cd "$OUT_ROOT" && sha256sum *.zip > SHA256SUMS.txt )
elif command -v shasum >/dev/null 2>&1; then
  ( cd "$OUT_ROOT" && shasum -a 256 *.zip > SHA256SUMS.txt )
fi
[ -f "$OUT_ROOT/SHA256SUMS.txt" ] && { echo; echo "  -> SHA256SUMS.txt"; sed 's/^/     /' "$OUT_ROOT/SHA256SUMS.txt"; }

echo
echo "=== 汇总（解压后 -> zip）==="
printf "%b" "$report"
printf "%-28s %sMB->%sMB\n" "FlexBot_BuiltInPlugins" "$plug_unzip_mb" "$plug_zip_mb"
ls -lh "$OUT_ROOT"/*.zip | awk '{print "  " $9 "  " $5}'
