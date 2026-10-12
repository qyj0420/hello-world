"""Only the three SciPy signatures used by frozen E, using NumPy float64.

Unsupported signatures fail explicitly. Not a general SciPy replacement.
"""
import numpy as np


def maximum_filter1d(values, size, mode='constant', cval=np.inf):
    x = np.asarray(values, dtype=np.float64)
    if x.ndim != 1 or size != 21 or mode != 'constant' or cval != np.inf:
        raise ValueError('E maximum filter supports size=21, constant +inf only')
    padded = np.pad(x, (10, 10), constant_values=cval)
    result = padded[:len(x)].copy()
    for offset in range(1, 21):
        np.maximum(result, padded[offset:offset+len(x)], out=result)
    return result


def find_peaks(values, height=None):
    x = np.asarray(values, dtype=np.float64)
    if x.ndim != 1 or not np.isfinite(x).all():
        raise ValueError('E peak input must be a finite 1-D array')
    if height is not None and (not np.isscalar(height) or not np.isfinite(height)):
        raise ValueError('E supports a finite scalar minimum peak height only')
    if len(x) < 3:
        return np.empty(0, dtype=np.intp), ({} if height is None else {'peak_heights':np.empty(0)})
    # A plateau is one peak at the lower midpoint, exactly the used SciPy rule.
    changes = np.flatnonzero(x[1:] != x[:-1])
    starts = np.r_[0, changes+1]
    ends = np.r_[changes, len(x)-1]
    valid = (starts > 0) & (ends < len(x)-1)
    starts, ends = starts[valid], ends[valid]
    valid = (x[starts] > x[starts-1]) & (x[ends] > x[ends+1])
    peaks = ((starts[valid]+ends[valid])//2).astype(np.intp)
    if height is not None:
        peaks = peaks[x[peaks] >= height]
    return peaks, ({} if height is None else {'peak_heights':x[peaks]})


def correlate(values, template, mode='full', method='auto'):
    x, y = np.asarray(values,dtype=np.float64), np.asarray(template,dtype=np.float64)
    if x.ndim != 1 or y.ndim != 1 or not len(x) or not len(y) or mode != 'full' or method != 'auto':
        raise ValueError('E correlation supports nonempty real 1-D full/auto only')
    if not np.isfinite(x).all() or not np.isfinite(y).all():
        raise ValueError('E correlation input must be finite')
    if len(x)*len(y) <= 10000000:
        return np.correlate(x,y,mode='full')
    n = len(x)+len(y)-1
    fft = 1 << (n-1).bit_length()
    return np.fft.irfft(np.fft.rfft(x,fft)*np.fft.rfft(y[::-1],fft),fft)[:n]
