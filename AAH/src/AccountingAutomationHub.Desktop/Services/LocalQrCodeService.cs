using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>Local, dependency-free QR encoder for compact AAH mobile enrollment payloads.
/// Fixed at QR Version 5-L (37x37, 108 data codewords + 26 ECC codewords), byte mode.
/// No network QR service is used and no credential/OTP is embedded in the image.
/// </summary>
public static class LocalQrCodeService
{
    private const int Size=37, DataCodewords=108, EccCodewords=26;
    public static ImageSource CreateImage(string payload,int scale=7)
    {
        var bytes=Encoding.UTF8.GetBytes(payload ?? string.Empty);
        if(bytes.Length>106)throw new InvalidOperationException("Mobile QR payload is too long. Shorten the server host or User ID.");
        var data=BuildData(bytes);var all=new byte[DataCodewords+EccCodewords];Array.Copy(data,all,DataCodewords);var ecc=ReedSolomon(data,EccCodewords);Array.Copy(ecc,0,all,DataCodewords,EccCodewords);
        var modules=new bool[Size,Size];var function=new bool[Size,Size];DrawFunctionPatterns(modules,function);PlaceData(modules,function,all);DrawFormat(modules,function,0);
        var quiet=4;var width=(Size+quiet*2)*scale;var pixels=new byte[width*width*4];
        for(int y=0;y<width;y++)for(int x=0;x<width;x++){var mx=x/scale-quiet;var my=y/scale-quiet;var black=mx>=0&&my>=0&&mx<Size&&my<Size&&modules[my,mx];var v=(byte)(black?0:255);var i=(y*width+x)*4;pixels[i]=v;pixels[i+1]=v;pixels[i+2]=v;pixels[i+3]=255;}
        var bitmap=BitmapSource.Create(width,width,96,96,PixelFormats.Bgra32,null,pixels,width*4);bitmap.Freeze();return bitmap;
    }
    private static byte[] BuildData(byte[] input){var bits=new List<bool>(DataCodewords*8);Put(bits,0b0100,4);Put(bits,input.Length,8);foreach(var b in input)Put(bits,b,8);for(int i=0;i<4&&bits.Count<DataCodewords*8;i++)bits.Add(false);while(bits.Count%8!=0)bits.Add(false);var result=new List<byte>();for(int i=0;i<bits.Count;i+=8){int v=0;for(int j=0;j<8;j++)v=(v<<1)|(bits[i+j]?1:0);result.Add((byte)v);}var pad=true;while(result.Count<DataCodewords){result.Add(pad?(byte)0xEC:(byte)0x11);pad=!pad;}return result.ToArray();}
    private static void Put(List<bool>b,int value,int count){for(int i=count-1;i>=0;i--)b.Add(((value>>i)&1)!=0);}
    private static void DrawFunctionPatterns(bool[,]m,bool[,]f){DrawFinder(m,f,3,3);DrawFinder(m,f,Size-4,3);DrawFinder(m,f,3,Size-4);for(int i=0;i<Size;i++){if(!f[6,i])Set(m,f,6,i,i%2==0);if(!f[i,6])Set(m,f,i,6,i%2==0);}DrawAlignment(m,f,30,30);for(int i=0;i<6;i++)Mark(f,i,8);Mark(f,7,8);Mark(f,8,8);Mark(f,8,7);for(int i=9;i<15;i++)Mark(f,8,14-i);for(int i=0;i<8;i++)Mark(f,8,Size-1-i);for(int i=8;i<15;i++)Mark(f,Size-15+i,8);Set(m,f,Size-8,8,true);}
    private static void DrawFinder(bool[,]m,bool[,]f,int cx,int cy){for(int dy=-4;dy<=4;dy++)for(int dx=-4;dx<=4;dx++){int x=cx+dx,y=cy+dy;if(x<0||y<0||x>=Size||y>=Size)continue;var d=Math.Max(Math.Abs(dx),Math.Abs(dy));Set(m,f,y,x,d!=2&&d!=4);}}
    private static void DrawAlignment(bool[,]m,bool[,]f,int cx,int cy){for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)Set(m,f,cy+dy,cx+dx,Math.Max(Math.Abs(dx),Math.Abs(dy))!=1);}
    private static void Mark(bool[,]f,int y,int x){if(y>=0&&x>=0&&y<Size&&x<Size)f[y,x]=true;}private static void Set(bool[,]m,bool[,]f,int y,int x,bool v){m[y,x]=v;f[y,x]=true;}
    private static void PlaceData(bool[,]m,bool[,]f,byte[]code){int bit=0;bool up=true;for(int right=Size-1;right>=1;right-=2){if(right==6)right--;for(int k=0;k<Size;k++){int y=up?Size-1-k:k;for(int dx=0;dx<2;dx++){int x=right-dx;if(f[y,x])continue;bool v=false;if(bit<code.Length*8)v=((code[bit>>3]>>(7-(bit&7)))&1)!=0;bit++;if(((x+y)&1)==0)v=!v;m[y,x]=v;}}up=!up;}}
    private static void DrawFormat(bool[,]m,bool[,]f,int mask){int data=(1<<3)|mask;int rem=data<<10;for(int i=14;i>=10;i--)if(((rem>>i)&1)!=0)rem^=0x537<<(i-10);int bits=((data<<10)|rem)^0x5412;for(int i=0;i<6;i++){bool v=((bits>>i)&1)!=0;m[i,8]=v;f[i,8]=true;}m[7,8]=((bits>>6)&1)!=0;m[8,8]=((bits>>7)&1)!=0;m[8,7]=((bits>>8)&1)!=0;f[7,8]=f[8,8]=f[8,7]=true;for(int i=9;i<15;i++){bool v=((bits>>i)&1)!=0;m[8,14-i]=v;f[8,14-i]=true;}for(int i=0;i<8;i++){bool v=((bits>>i)&1)!=0;m[8,Size-1-i]=v;f[8,Size-1-i]=true;}for(int i=8;i<15;i++){bool v=((bits>>i)&1)!=0;m[Size-15+i,8]=v;f[Size-15+i,8]=true;}}
    private static byte[] ReedSolomon(byte[]data,int degree){var gen=new byte[]{1};for(int i=0;i<degree;i++){var next=new byte[gen.Length+1];var root=Pow2(i);for(int j=0;j<gen.Length;j++){next[j]^=gen[j];next[j+1]^=Mul(gen[j],root);}gen=next;}var result=new byte[degree];foreach(var b in data){var factor=(byte)(b^result[0]);for(int i=0;i<degree-1;i++)result[i]=result[i+1];result[degree-1]=0;for(int i=0;i<degree;i++)result[i]^=Mul(gen[i+1],factor);}return result;}
    private static byte Pow2(int e){int x=1;for(int i=0;i<e;i++){x<<=1;if((x&0x100)!=0)x^=0x11D;}return(byte)x;}private static byte Mul(byte a,byte b){int z=0,x=a,y=b;while(y!=0){if((y&1)!=0)z^=x;y>>=1;x<<=1;if((x&0x100)!=0)x^=0x11D;}return(byte)z;}
}
