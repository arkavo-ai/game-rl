// macOS native plugin for IOSurface-backed vision streaming

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <IOSurface/IOSurface.h>
#import <CoreVideo/CoreVideo.h>

static id<MTLDevice> g_device = nil;
static id<MTLCommandQueue> g_queue = nil;
static NSMutableDictionary<NSNumber *, id<MTLTexture>> *g_textures = nil;
static NSMutableDictionary<NSNumber *, NSValue *> *g_surfaces = nil;

static void ensure_device()
{
    if (g_device != nil)
    {
        return;
    }

    g_device = MTLCreateSystemDefaultDevice();
    if (g_device == nil)
    {
        return;
    }

    g_queue = [g_device newCommandQueue];
    g_textures = [[NSMutableDictionary alloc] init];
    g_surfaces = [[NSMutableDictionary alloc] init];
}

extern "C" uint64_t gamerl_create_iosurface(int width, int height)
{
    @autoreleasepool
    {
        ensure_device();
        if (g_device == nil)
        {
            return 0;
        }

        NSDictionary *props = @{
            (NSString *)kIOSurfaceWidth : @(width),
            (NSString *)kIOSurfaceHeight : @(height),
            (NSString *)kIOSurfaceBytesPerElement : @(4),
            (NSString *)kIOSurfaceBytesPerRow : @(width * 4),
            (NSString *)kIOSurfacePixelFormat : @(kCVPixelFormatType_32BGRA),
            (NSString *)kIOSurfaceIsGlobal : @YES
        };

        IOSurfaceRef surface = IOSurfaceCreate((__bridge CFDictionaryRef)props);
        if (surface == nil)
        {
            return 0;
        }

        uint32_t surface_id = IOSurfaceGetID(surface);
        MTLTextureDescriptor *desc = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatBGRA8Unorm
                                                                                        width:(NSUInteger)width
                                                                                       height:(NSUInteger)height
                                                                                    mipmapped:NO];
        desc.usage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;

        id<MTLTexture> texture = [g_device newTextureWithDescriptor:desc iosurface:surface plane:0];
        if (texture == nil)
        {
            CFRelease(surface);
            return 0;
        }

        NSNumber *key = @(surface_id);
        g_textures[key] = texture;
        g_surfaces[key] = [NSValue valueWithPointer:surface];

        return surface_id;
    }
}

extern "C" int gamerl_update_iosurface(void *source_texture_ptr, uint64_t surface_id)
{
    @autoreleasepool
    {
        ensure_device();
        if (g_device == nil || g_queue == nil || source_texture_ptr == nil)
        {
            return 0;
        }

        NSNumber *key = @(surface_id);
        id<MTLTexture> destination = g_textures[key];
        if (destination == nil)
        {
            return 0;
        }

        id<MTLTexture> source = (__bridge id<MTLTexture>)source_texture_ptr;
        if (source == nil)
        {
            return 0;
        }

        id<MTLCommandBuffer> command_buffer = [g_queue commandBuffer];
        id<MTLBlitCommandEncoder> blit = [command_buffer blitCommandEncoder];

        NSUInteger width = MIN(source.width, destination.width);
        NSUInteger height = MIN(source.height, destination.height);
        MTLSize size = MTLSizeMake(width, height, 1);

        [blit copyFromTexture:source
                  sourceSlice:0
                  sourceLevel:0
                 sourceOrigin:MTLOriginMake(0, 0, 0)
                   sourceSize:size
                    toTexture:destination
             destinationSlice:0
             destinationLevel:0
            destinationOrigin:MTLOriginMake(0, 0, 0)];
        [blit endEncoding];

        [command_buffer commit];
        [command_buffer waitUntilCompleted];

        return 1;
    }
}

extern "C" void gamerl_release_iosurface(uint64_t surface_id)
{
    @autoreleasepool
    {
        NSNumber *key = @(surface_id);
        NSValue *value = g_surfaces[key];
        if (value != nil)
        {
            IOSurfaceRef surface = (IOSurfaceRef)[value pointerValue];
            if (surface != nil)
            {
                CFRelease(surface);
            }
        }

        [g_surfaces removeObjectForKey:key];
        [g_textures removeObjectForKey:key];
    }
}
