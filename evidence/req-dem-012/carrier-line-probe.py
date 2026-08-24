import numpy as np

sps=16; N=4000; alpha=0.35; span=20
def rrc(t,a):
    out=np.zeros_like(t)
    for i,x in enumerate(t):
        if abs(x)<1e-9: out[i]=1.0+a*(4/np.pi-1); continue
        if a>1e-9 and abs(abs(x)-1/(4*a))<1e-7:
            ang=np.pi/(4*a)
            out[i]=(a/np.sqrt(2))*((1+2/np.pi)*np.sin(ang)+(1-2/np.pi)*np.cos(ang)); continue
        num=np.sin(np.pi*x*(1-a))+4*a*x*np.cos(np.pi*x*(1+a))
        out[i]=num/(np.pi*x*(1-(4*a*x)**2))
    return out

t=np.arange(-span*sps,span*sps+1)/sps
p=rrc(t,alpha)

def signal(syms):
    x=np.zeros(N*sps,dtype=complex)
    for k,s in enumerate(syms):
        c=k*sps
        lo=max(0,c-span*sps); hi=min(len(x),c+span*sps+1)
        x[lo:hi]+= s*p[lo-c+span*sps:hi-c+span*sps]
    return x

rng=np.random.default_rng(7)

def peak(x,M,label):
    y=x**M
    Y=np.fft.fftshift(np.abs(np.fft.fft(y,1<<16)))
    f=np.fft.fftshift(np.fft.fftfreq(1<<16,d=1/sps))  # in units of symbol rate
    i=np.argmax(Y)
    # top 4 distinct peaks
    order=np.argsort(Y)[::-1]
    tops=[]
    for j in order:
        if all(abs(f[j]-fp)>0.02 for fp,_ in tops):
            tops.append((f[j],Y[j]/Y[i]))
        if len(tops)>=4: break
    print(f"{label}: peak at {f[i]:+.4f} Rs (=> carrier {f[i]/M:+.4f} Rs); tops " +
          ", ".join(f"{a:+.3f}Rs:{b:.2f}" for a,b in tops))

# 8PSK: independent points on the 8-ring
s8=np.exp(1j*2*np.pi*rng.integers(0,8,N)/8)
peak(signal(s8),8,"8PSK        ")

# pi/4-DQPSK: QPSK points turned by k*pi/4
q=np.exp(1j*(np.pi/4+np.pi/2*rng.integers(0,4,N)))
s4=q*np.exp(1j*np.pi/4*np.arange(N))
peak(signal(s4),8,"PI4DQPSK    ")
peak(signal(s4*np.exp(-1j*np.pi/4*np.arange(N))),4,"PI4 derotated")

# QPSK for reference
peak(signal(q),4,"QPSK        ")
