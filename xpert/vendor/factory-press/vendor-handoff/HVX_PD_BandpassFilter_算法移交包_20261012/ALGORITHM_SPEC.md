# 局放零相位线性带通滤波算法规范

## 接口

```text
Filter(waveform, sampleRateHz, lowCutoffHz, highCutoffHz) -> filteredWaveform
```

- `waveform`：一维双精度数组。
- `sampleRateHz`：采样率，单位 Hz，重点支持 125 MS/s 和 250 MS/s。
- `lowCutoffHz`、`highCutoffHz`：物理截止频率，单位 Hz。
- 输出数组点数必须与输入完全一致。

## 处理链

1. 校验参数和全部样本。
2. 计算与低截止频率相关的奇对称延拓长度。
3. 对输入首尾做奇对称延拓。
4. 根据本次传入采样率设计四阶 Butterworth 高通和四阶 Butterworth 低通，共四个二阶节。
5. 顺序执行四个二阶节。
6. 反转数组，重复执行相同的四个二阶节，再反转回来。
7. 去除延拓区，返回与输入同长度的结果。

## Butterworth 二阶节

四阶 Butterworth 分解为两个二阶节，其 Q 值为：

```text
Q1 = 0.541196100146197
Q2 = 1.306562964876377
```

令 `K = tan(pi * fc / fs)`，低通节：

```text
norm = 1 / (1 + K/Q + K^2)
b0 = K^2 * norm
b1 = 2*b0
b2 = b0
a1 = 2*(K^2 - 1)*norm
a2 = (1 - K/Q + K^2)*norm
```

高通节：

```text
norm = 1 / (1 + K/Q + K^2)
b0 = norm
b1 = -2*norm
b2 = norm
a1 = 2*(K^2 - 1)*norm
a2 = (1 - K/Q + K^2)*norm
```

差分方程按以下符号约定：

```text
y[n] = b0*x[n] + b1*x[n-1] + b2*x[n-2] - a1*y[n-1] - a2*y[n-2]
```

实现使用 Direct Form II Transposed，以减少状态量并提高数值稳定性。

## 零相位截止点补偿

正向和反向两次滤波会使最终幅值等于单程幅值的平方。四阶 Butterworth 的补偿因子为：

```text
c = (sqrt(2) - 1)^(1/(2*4))
```

数字预畸变参数：

```text
lowK  = tan(pi * lowCutoffHz  / sampleRateHz) * c
highK = tan(pi * highCutoffHz / sampleRateHz) / c
```

这样可使最终零相位响应在指定低、高截止频率附近约为 -3 dB。高、低截止频率过近时，两侧滤波器相互作用会使实际总响应产生偏差，应通过频响验收确认。

## 奇对称延拓

```text
pad = min(N-1, max(48, ceil(3*sampleRateHz/lowCutoffHz)))
left[k]  = 2*x[0]   - x[k]
right[k] = 2*x[N-1] - x[N-1-k]
```

其中 `k=1..pad`。延拓长度由物理低截止频率决定，而不是由总记录时长决定，因此 100 us 和 50 ms 使用同一物理滤波器。

## 重要性质和限制

- 零相位：脉冲主峰不产生固定群时延。
- 线性：对固定参数、固定长度和固定边界规则，满足叠加和齐次性。
- 非因果：需要完整记录后才能处理，不能直接用于实时逐点输出。
- 边界效应：时间平移不变性只适用于远离首尾的主体区；首尾由延拓规则决定。
- 内存：当前实现会创建延拓数组和输出数组，50 ms、250 MS/s 时应预留足够内存。
- 频率约束：`0 < lowCutoffHz < highCutoffHz < sampleRateHz/2`。

## 必测用例

- 125 MS/s，100 us：12500 点。
- 125 MS/s，50 ms：6250000 点。
- 250 MS/s，100 us：25000 点。
- 250 MS/s，50 ms：12500000 点。
- 正弦混合信号：通带幅值保持，低、高阻带衰减。
- 单位冲击：输出绝对峰值索引与输入冲击索引一致。
- 长短记录一致性：排除端点区后，相同数据段的相对 RMS 差异小于 2%。
- 非法参数：高截止超过奈奎斯特频率、NaN、Infinity、低截止非正等均应拒绝。
