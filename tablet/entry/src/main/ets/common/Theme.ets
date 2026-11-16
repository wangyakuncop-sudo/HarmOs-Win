// 设备端统一视觉基准（供 Index / NetworkProbePage / SessionPage 三页共用）。
// 只放纯常量与纯函数：@Styles / @Builder 必须留在各页内（跨文件导出的全局 @Styles
// 在 ArkTS 里行为不稳定，且无法引用本页的 @State），所以这里不定义任何装饰器成员。
//
// 配色思路：浅色玻璃底 + 白色胶囊卡片。运行态用系统红（#E84026），
// 暂停/等待用琥珀（#F0A400），正常/直发用绿（#12B76A），停用态用中性灰。

export const C_BG_TOP: string = '#F5F8FD';
export const C_BG_BOTTOM: string = '#E7EDF7';
export const C_CARD: string = '#FFFFFF';
export const C_TITLE: string = '#101828';
export const C_BODY: string = '#475467';
export const C_SUB: string = '#98A2B3';
export const C_PRIMARY: string = '#0A59F7';
export const C_DANGER: string = '#E84026';
export const C_WARN: string = '#F0A400';
export const C_OK: string = '#12B76A';
export const C_NEUTRAL: string = '#C4CBD6';

// 卡片内嵌的小色块底（指标卡 / 信息标签 / 次级按钮）
export const C_FILL_SOFT: string = '#F7F9FC';
export const C_FILL_CHIP: string = '#F2F5FA';
export const C_FILL_BLUE: string = '#E8F0FE';
export const C_FILL_GREEN: string = '#E7F8EF';
export const C_FILL_DARK: string = '#1D2939';

// 统一阴影（白卡浮在浅色渐变底上）
export const SHADOW_CARD: ShadowOptions = {
  radius: 18,
  color: 'rgba(16, 24, 40, 0.06)',
  offsetX: 0,
  offsetY: 6
};

export const SHADOW_CHIP: ShadowOptions = {
  radius: 10,
  color: 'rgba(16, 24, 40, 0.06)',
  offsetX: 0,
  offsetY: 3
};

// 日志染色：错误红、警告琥珀、其余正文色
export function logColor(line: string): string {
  if (line.indexOf('[ERR]') >= 0) {
    return C_DANGER;
  }
  if (line.indexOf('[WARN]') >= 0) {
    return C_WARN;
  }
  return C_BODY;
}

// 秒级时间戳前缀，三个页面日志格式一致
export function timePrefix(): string {
  const d: Date = new Date();
  const p2 = (n: number): string => n < 10 ? `0${n}` : `${n}`;
  return `${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`;
}
