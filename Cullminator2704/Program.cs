#define SIMD

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;


struct Sphere
{
#if SIMD
    public Vector128<float> simdCacheX;
    public Vector128<float> simdCacheY;
    public Vector128<float> simdCacheZ;
    public Vector128<float> simdCacheR;
#else
    public Vector3 position;
#endif

    public float radius;

#if SIMD
    public void UpdateCaches(Vector3 position)
    {
        simdCacheX = Sse.SetAllVector128(position.X);
        simdCacheY = Sse.SetAllVector128(position.Y);
        simdCacheZ = Sse.SetAllVector128(position.Z);
        simdCacheR = Sse.SetAllVector128(-radius);
    }
#endif
}

struct Frustum
{
    public float fov;
    public float nearPlane;
    public float farPlane;
    public float aspectRatio;

    public Matrix4x4 GetFrustumMatrix(Matrix4x4 viewMatrix)
    {
        var pm = Matrix4x4.CreatePerspectiveFieldOfView(fov / 57f, aspectRatio, nearPlane, farPlane);
        Matrix4x4.Invert(viewMatrix, out Matrix4x4 invView);
        Vector3 nm = invView.Translation;
        invView.Translation = Vector3.Zero;
        viewMatrix.Translation = nm;
        Matrix4x4 vpm = pm * viewMatrix;
        return vpm;
    }
};

struct Planes
{
    public Vector128<float> x, y, z, w;
}

class Program
{
    Sphere[] spheres;
    private Frustum culler;

    public Program()
    {
        //add stuff to cull
        const int width = 100; //100
        const int height = 50; //6
        const float spacing = 2;

        spheres = new Sphere[width * width * height];

        var r = new Random();

        int c = 0;
        for (int z = 0; z < width; z++)
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var position = new Vector3(
                        (x - (width / 2)) * spacing,
                        (y - (height / 2)) * spacing,
                        (z - (width / 2)) * spacing);

                    var radius = ((20 + r.Next()) % 100) / 100f;
                    var s = new Sphere { radius = radius };

#if SIMD
                    s.UpdateCaches(position);
#else
                    s.position = position;
#endif

                    spheres[c++] = s;
                }

        //add cullers
        culler = new Frustum { fov = 50, nearPlane = 0.01f, farPlane = 500, aspectRatio = 2 };
    }

    bool NaiveCull(in Vector3 pos, float radius, in Vector4 plane)
    {
        return plane.X * pos.X + plane.Y * pos.Y + plane.Z * pos.Z + plane.W <= -radius;
    }

    static Vector128<float> SseAdd(in Vector128<float> a, in Vector128<float> b, in Vector128<float> c, in Vector128<float> d)
    {
        return Sse.Add(Sse.Add(a, b), Sse.Add(c, d));
    }

    static Vector128<float> SseSetBw(float x, float y, float z, float w)
    {
        return Sse.SetVector128(w, z, y, x);
    }


    private bool draw = true;
    private ulong culled = 0;
    private ulong inView = 0;

#if SIMD
    void simdCull(in Sphere s, Planes planes)
    {
        var xs = Sse.Multiply(planes.x, s.simdCacheX);
        var ys = Sse.Multiply(planes.y, s.simdCacheY);
        var zs = Sse.Multiply(planes.z, s.simdCacheZ);

        var added = SseAdd(xs, ys, zs, planes.w);

        var results = Sse.CompareLessThan(added, s.simdCacheR);

        var cull = Sse.MoveMask(results);

        if (draw)
        {
            if (cull != 0)
                culled++;
            else
                inView++;
        }
    }
#else
        void naiveCull(in Sphere s, in Vector4 left, in Vector4 right, in Vector4 top, in Vector4 bottom)
    {
        bool cull = false;

        var pos = s.position;

        if (NaiveCull(pos, s.radius, right)) cull = true;
        else if (NaiveCull(pos, s.radius, left)) cull = true;
        else if (NaiveCull(pos, s.radius, bottom)) cull = true;
        else if (NaiveCull(pos, s.radius, top)) cull = true;

        if (draw)
        {
            if (cull)
                culled++;
            else
                inView++;
        }
    }

#endif

    float Update()
    {
        culled = inView = 0;

        Matrix4x4.Invert(Matrix4x4.Identity, out Matrix4x4 frustumMat4);
        Matrix4x4 m = culler.GetFrustumMatrix(frustumMat4);

        Vector4 right;
        right.X = m.M14 + m.M11;
        right.Y = m.M24 + m.M21;
        right.Z = m.M34 + m.M31;
        right.W = m.M44 + m.M41;

        Vector4 left;
        left.X = m.M14 - m.M11;
        left.Y = m.M24 - m.M21;
        left.Z = m.M34 - m.M31;
        left.W = m.M44 - m.M41;

        Vector4 top;
        top.X = m.M14 - m.M12;
        top.Y = m.M24 - m.M22;
        top.Z = m.M34 - m.M32;
        top.W = m.M44 - m.M42;

        Vector4 bottom;
        bottom.X = m.M14 + m.M12;
        bottom.Y = m.M24 + m.M22;
        bottom.Z = m.M34 + m.M32;
        bottom.W = m.M44 + m.M42;


        Planes planes = new Planes {
            x = SseSetBw(left.X, right.X, top.X, bottom.X),
            y = SseSetBw(left.Y, right.Y, top.Y, bottom.Y),
            z = SseSetBw(left.Z, right.Z, top.Z, bottom.Z),
            w = SseSetBw(left.W, right.W, top.W, bottom.W),
        };

        var tp = Stopwatch.StartNew();

        if (true) //simd)
        {
            for (var i = 0; i < spheres.Length; i++)
            //Parallel.For(0, spheres.Length, (i, e) =>
            {
#if SIMD
                simdCull(spheres[i], planes);
#else
                naiveCull(spheres[i], left, right, top, bottom);
#endif
            }
            //);
        }
        else
        {

            //registry.view<BSphere, Transform>().each([this, &left, &right, &top, &bottom](auto entity, BSphere & s, Transform & t) {
            //    bool cull = false;

            //    auto & pos = t.position;

            //    if (NaiveCull(pos, s.radius, right)) cull = true;
            //    else if (NaiveCull(pos, s.radius, left)) cull = true;
            //    else if (NaiveCull(pos, s.radius, bottom)) cull = true;
            //    else if (NaiveCull(pos, s.radius, top)) cull = true;

            //    if (draw)
            //    {
            //        if (cull)
            //        {
            //            if (drawCulled)
            //            {
            //                drawListMtx.lock () ;
            //                culled.emplace_back(s);
            //                drawListMtx.unlock();
            //            }
            //        }
            //        else
            //        {
            //            drawListMtx.lock () ;
            //            inView.emplace_back(s);
            //            drawListMtx.unlock();
            //        }
            //    }
            //});

            ////             if (mt)
            ////             std::for_each(std::execution::par, view.begin(), view.end(), [&view, &right, &left, &bottom, &top](const auto entity) {
            ////                 BSphere& s = view.get(entity);
            //// //                naiveCull(s, left, right, top, bottom);
            ////             });
            ////             else
            ////             registry.view<BSphere>().each([this, &left, &right, &top, &bottom](auto entity, BSphere& s) {
            ////                 //naiveCull(s, left, right, top, bottom);
            ////             });
        }

        var el = (int)((tp.ElapsedTicks / (float)Stopwatch.Frequency) * 1000000f);
        var ms = tp.ElapsedMilliseconds;

        Console.WriteLine($"time: {el}, ms {ms}");

        if (draw)
            Console.WriteLine($"culled: {culled}, in view: {inView}");

        return el;
    }

    static void Main(string[] args)
    {
        var p = new Program();

        List<float> results = new List<float>();

        for (int i = 0; i < 30; i++)
        {
            var result = p.Update();
            if(i>=5) //discard first 5
                results.Add(result);
        }
        
        Console.WriteLine($"########### average time (5th onwards): {results.Sum()/results.Count}");

        Console.ReadKey();
    }
}

