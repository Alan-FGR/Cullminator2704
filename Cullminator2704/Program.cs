using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

struct Sphere
{
    public Vector128<float> simdCacheX;
    public Vector128<float> simdCacheY;
    public Vector128<float> simdCacheZ;
    public Vector128<float> simdCacheR;
    public float radius;

    public void UpdateCaches(Vector3 position)
    {
        simdCacheX = Sse.SetAllVector128(position.X);
        simdCacheY = Sse.SetAllVector128(position.Y);
        simdCacheZ = Sse.SetAllVector128(position.Z);
        simdCacheR = Sse.SetAllVector128(-radius);
    }
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


class Program
{
    List<Sphere> spheres = new List<Sphere>();
    private Frustum culler;

    public Program()
    {
        //add stuff to cull
        const int width = 100; //100
        const int height = 100; //6
        const float spacing = 2;

        var r = new Random();

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
                    s.UpdateCaches(position);
                    spheres.Add(s);
                }

        //add cullers
        culler = new Frustum { fov = 50, nearPlane = 0.01f, farPlane = 500, aspectRatio = 2 };
    }

    //bool NaiveCull(Vector3 pos, float& radius, vec4& plane)
    //{
    //    return plane.x * pos.x + plane.y * pos.y + plane.z * pos.z + plane.w <= -radius;
    //}

    static Vector128<float> SseAdd(Vector128<float> a, Vector128<float> b, Vector128<float> c, Vector128<float> d)
    {
        return Sse.Add(Sse.Add(a, b), Sse.Add(c, d));
    }

    static Vector128<float> SseSetBw(float x, float y, float z, float w)
    {
        return Sse.SetVector128(w, z, y, x);
    }

    //void naiveCull(BSphere& s, vec4 &left, vec4 &right, vec4 &top, vec4 &bottom)
    //{
    //    auto[pos, r] = s.GetCachedDataSlow();

    //    bool cull = false;

    //    if (NaiveCull(pos, s.radius, right)) cull = true;
    //    else if (NaiveCull(pos, s.radius, left)) cull = true;
    //    else if (NaiveCull(pos, s.radius, bottom)) cull = true;
    //    else if (NaiveCull(pos, s.radius, top)) cull = true;

    //    if (draw)
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
    //}

    private bool draw = true;
    private ulong culled = 0;
    private ulong inView = 0;

    void simdCull(in Sphere s, in List<Vector128<float>> planes)
    {
        var xs = Sse.Multiply(planes[0], s.simdCacheX);
        var ys = Sse.Multiply(planes[1], s.simdCacheY);
        var zs = Sse.Multiply(planes[2], s.simdCacheZ);

        var added = SseAdd(xs, ys, zs, planes[3]);

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

    void Update()
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


        List<Vector128<float>> planes = new List<Vector128<float>> {
            SseSetBw(left.X, right.X, top.X, bottom.X),
            SseSetBw(left.Y, right.Y, top.Y, bottom.Y),
            SseSetBw(left.Z, right.Z, top.Z, bottom.Z),
            SseSetBw(left.W, right.W, top.W, bottom.W),
        };

        var tp = Stopwatch.StartNew();

        if (true) //simd)
        {
            foreach (Sphere sphere in spheres)
            {
                simdCull(sphere, planes);
            }
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

        Console.WriteLine("#########################################");
        Console.WriteLine($"time: {el}, ms {ms}");

        if (draw)
            Console.WriteLine($"culled: {culled}, in view: {inView}");
    }
    
    static void Main(string[] args)
    {
        var p = new Program();

        for (int i = 0; i < 10; i++)
        {
            p.Update();
        }

        Console.ReadKey();
    }
}

