from PIL import Image, ImageDraw
import math
def logo(S):
    sc=4; W=S*sc
    im=Image.new('RGBA',(W,W),(0,0,0,0))
    grad=Image.new('L',(W,W)); grad.putdata([int(255*(x+y)/(2*W-2)) for y in range(W) for x in range(W)])
    c1=(0x6E,0x4B,0xFF,255); c2=(0x43,0x26,0xD9,255)
    tile=Image.composite(Image.new('RGBA',(W,W),c2),Image.new('RGBA',(W,W),c1),grad)
    mask=Image.new('L',(W,W),0); ImageDraw.Draw(mask).rounded_rectangle([0,0,W-1,W-1],radius=int(W*0.22),fill=255)
    im.paste(tile,(0,0),mask)
    d=ImageDraw.Draw(im)
    w=W; cx=w/2; cy=w*0.60; r=w*0.30; lw=int(w*0.085)
    d.arc([cx-r,cy-r,cx+r,cy+r],180,360,fill='white',width=lw)
    for a in (180,360):
        x=cx+math.cos(math.radians(a))*(r-lw/2); y=cy+math.sin(math.radians(a))*(r-lw/2)
        d.ellipse([x-lw/2,y-lw/2,x+lw/2,y+lw/2],fill='white')
    ang=math.pi*1.25; ex=cx+math.cos(ang)*r*0.8; ey=cy+math.sin(ang)*r*0.8
    d.line([cx,cy,ex,ey],fill='white',width=lw); d.ellipse([ex-lw/2,ey-lw/2,ex+lw/2,ey+lw/2],fill='white')
    d.ellipse([cx-w*0.07,cy-w*0.07,cx+w*0.07,cy+w*0.07],fill='white')
    return im.resize((S,S),Image.LANCZOS)
big=logo(256); big.save('res/nexlimit-256.png')
big.save('res/nexlimit.ico',sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(256,256)],append_images=[logo(s) for s in (16,20,24,32,40,48,64)])
